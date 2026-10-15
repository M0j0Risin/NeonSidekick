using System.Runtime.Versioning;
using NeonSidekick.Sql;

namespace NeonSidekick.EventLog;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsEventLogSystem
{
    /// <summary>The auth method's word as <c>EVT_RPC_LOGIN_FLAGS</c>; <c>default</c> (or anything unknown) is Windows' own choice.</summary>
    internal static int AuthFlags(string? method) => method?.Trim().ToLowerInvariant() switch
    {
        "negotiate" => EventLogNative.RpcLoginAuthNegotiate,
        "kerberos" => EventLogNative.RpcLoginAuthKerberos,
        "ntlm" => EventLogNative.RpcLoginAuthNtlm,
        _ => EventLogNative.RpcLoginAuthDefault,
    };

    /// <summary>
    /// The session a read of <paramref name="target"/> runs in: 0 (the local one) for this PC and a file; for a remote host one opened
    /// now with <c>EvtRpcLogin</c> — as the user, or as the target's account, its <c>DOMAIN\name</c> split for the struct and a UPN passed
    /// whole with a null domain. Opening it checks nothing on the wire: a bad host or account fails at the session's first real call
    /// (the spike, 2026-10-15). The caller closes it.
    /// </summary>
    private static unsafe nint OpenSession(EventLogTarget target)
    {
        if (target.Kind != EventLogTargetKind.Remote)
        {
            return 0;
        }

        string host = target.Host ?? throw new EventLogException(87, "A remote target needs a host.");
        string? user = null;
        string? domain = null;
        string? password = null;
        int flags = EventLogNative.RpcLoginAuthDefault;
        if (target.Login is { } login)
        {
            if (WindowsCredentials.SplitAccount(login.User) is not { } split)
            {
                throw new EventLogException(1326, SqlText.RunAsNeedsDomain(login.User));
            }

            (domain, user) = split;
            password = login.Password;
            flags = AuthFlags(login.AuthMethod);
        }

        nint session;
        fixed (char* serverPointer = host)
        fixed (char* userPointer = user)
        fixed (char* domainPointer = domain)       // a UPN has no domain: fixed over null is a null pointer, which EvtRpcLogin wants
        fixed (char* passwordPointer = password)
        {
            var rpc = new EventLogNative.EvtRpcLogin
            {
                Server = serverPointer,
                User = userPointer,
                Domain = domainPointer,
                Password = passwordPointer,
                Flags = flags,
            };
            session = EventLogNative.EvtOpenSession(EventLogNative.RpcLogin, &rpc, 0, 0);
        }

        return session != 0 ? session : throw Failure("EvtOpenSession");
    }
}
