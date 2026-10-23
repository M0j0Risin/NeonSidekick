using System.Globalization;

namespace NeonSidekick.Services;

/// <summary>Where a failure happened, for its sentence: the computer as named, the service, whether remote, and the account.</summary>
public sealed record ServiceErrorContext(string Computer, string? Service, bool Remote, string Account, bool Write);

/// <summary>
/// A Win32 code from the Service Control Manager as a sentence (2026-10-15, the admin-tools plan, <c>EventLogErrors</c>' shape). Access denied
/// (5) is worded per target: locally a change needs the app run as administrator, or a service whose permissions allow you (the app never
/// elevates itself — that would be a process start); remotely the account needs rights there, and a local administrator's network sign-in is
/// filtered by UAC (remote restrictions). Pure.
/// </summary>
public static class ServiceErrors
{
    /// <summary>The app's own code (no Win32 code is negative) for a service whose configuration changed between the question and the change (C2).</summary>
    public const int ChangedSinceAsked = -1001;

    public static string Sentence(int code, ServiceErrorContext context, string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        string service = context.Service ?? "that service";
        return code switch
        {
            // A change refused remotely is the service's own permissions (2026-10-23, the admin review's J7: it got the reading sentence).
            5 when context.Remote && context.Write => $"Error: {context.Computer} refused the change to {service}: {context.Account} needs rights on that service there (its own permissions), and a local administrator's sign-in over the network is filtered by UAC's remote restrictions.",
            5 when context.Remote => $"Error: {context.Computer} refused {context.Account}. Reading its services needs rights on its Service Control Manager (a non-administrator is refused unless the SCM's security grants a group read), and a local administrator's sign-in over the network is filtered by UAC's remote restrictions; a domain account in the Administrators group works.",
            5 when context.Write => $"Error: Windows refused the change to {service}: changing this service needs the app run as administrator, or a service whose permissions allow you.",
            5 => $"Error: Windows refused access to {service} on {context.Computer}.",
            ChangedSinceAsked => $"Error: {service} on {context.Computer} changed after the question (its program, account or DLL is not the one shown), so nothing was done; ask again.",
            1060 => $"Error: there is no service named '{service}' on {context.Computer}; service_list lists them.",
            1056 => $"Error: {service} is already running.",
            1062 => $"Error: {service} is not running.",
            1051 => $"Error: {service} cannot be stopped while services that depend on it run.",
            1058 => $"Error: {service} is disabled; change its start type before starting it.",
            1061 => $"Error: {service} cannot take that request now; it may be starting or stopping.",
            1052 => $"Error: {service} does not accept a stop.",
            1053 => $"Error: {service} did not answer the request in time.",
            1068 => $"Error: {service} could not start because a service it depends on did not.",
            1069 => $"Error: {service} could not start: its account could not sign in.",
            1072 => $"Error: {service} is marked for deletion.",
            1722 or 1726 or 53 => $"Error: {context.Computer} did not answer the Service Control Manager's call. Check the name, and that the Remote Service Management firewall rule is on there.",
            1326 => $"Error: {context.Computer} refused the sign-in as {context.Account}: the user name or password is wrong.",
            1311 => $"Error: no logon server could check the sign-in to {context.Computer}.",
            _ => $"Error: the Service Control Manager on {context.Computer} failed (code {code.ToString(CultureInfo.InvariantCulture)})" + (detail.Length > 0 ? ": " + detail : "."),
        };
    }
}
