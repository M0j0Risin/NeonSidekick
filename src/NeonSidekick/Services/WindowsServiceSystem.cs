using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NeonSidekick.Sql;

namespace NeonSidekick.Services;

/// <summary>
/// The Service Control Manager in-process (2026-10-15, the admin-tools plan) over <see cref="ServiceNative"/>. <c>OpenSCManagerW</c> takes no
/// credentials, so a remote <c>runas</c> signs in with a <c>NEW_CREDENTIALS</c> token (<see cref="WindowsCredentials.LogonNetOnly"/>) and runs
/// the whole open-and-work inside <see cref="WindowsIdentity.RunImpersonated{T}(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle, Func{T})"/>
/// on the caller's thread, which owns and disposes the token (<c>Unc/UncAccess</c>'s shape). Remote runas was built to the documented API and
/// never run here (no second machine; <c>.notes/remote_admin_todo.md</c>): Microsoft's "Services and RPC/TCP" page says RPC/TCP does not carry
/// <c>net use</c> credentials — a NEW_CREDENTIALS token is not a <c>net use</c> and should carry them, unproven; the fallback is the named-pipe
/// transport (<c>SCMApiConnectionParam</c>). Every failure is a <see cref="ServiceException"/> with its Win32 code. Excluded from coverage:
/// proven by the smoke's <c>services:scm</c> and <c>services:remote</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceSystem : IServiceSystem
{
    public T Run<T>(ServiceTarget target, Func<IServiceSession, T> work)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(work);
        if (target.Login is not { } login)
        {
            return Open(target.Host, work);
        }

        using var token = WindowsCredentials.LogonNetOnly(login.User, login.Password, out string? refused);
        if (token is null)
        {
            throw new ServiceException(1326, refused ?? "the sign-in was refused");
        }

        return WindowsIdentity.RunImpersonated(token, () => Open(target.Host, work));
    }

    private static T Open<T>(string? host, Func<IServiceSession, T> work)
    {
        string? machine = host is null ? null : host.StartsWith(@"\\", StringComparison.Ordinal) ? host : @"\\" + host;
        nint manager = ServiceNative.OpenSCManager(machine, null, ServiceNative.ScManagerConnect | ServiceNative.ScManagerEnumerateService);
        if (manager == 0)
        {
            throw Failure("OpenSCManagerW");
        }

        try
        {
            return work(new Session(manager));
        }
        finally
        {
            ServiceNative.CloseServiceHandle(manager);
        }
    }

    private static ServiceException Failure(string call)
    {
        int code = Marshal.GetLastPInvokeError();
        return new ServiceException(code, call + " failed: " + Marshal.GetPInvokeErrorMessage(code));
    }

    private sealed unsafe class Session(nint manager) : IServiceSession
    {
        public IReadOnlyList<ServiceEntry> List(bool drivers)
        {
            uint type = ServiceKinds.Win32 | (drivers ? ServiceKinds.Driver : 0);
            var entries = new List<ServiceEntry>(512);
            uint resume = 0;
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                bool done;
                uint needed, returned;
                fixed (byte* p = buffer)
                {
                    done = ServiceNative.EnumServicesStatusEx(manager, ServiceNative.ScEnumProcessInfo, type, ServiceNative.ServiceStateAll, p, (uint)buffer.Length, out needed, out returned, ref resume, null);
                    int error = done ? 0 : Marshal.GetLastPInvokeError();
                    if (!done && error != ServiceNative.ErrorMoreData)
                    {
                        throw new ServiceException(error, "EnumServicesStatusExW failed: " + Marshal.GetPInvokeErrorMessage(error));
                    }

                    var items = (ServiceNative.EnumServiceStatusProcess*)p;
                    for (int i = 0; i < returned; i++)
                    {
                        var s = items[i].Status;
                        entries.Add(new ServiceEntry(Text(items[i].ServiceName), Text(items[i].DisplayName), (ServiceRunState)s.CurrentState, (int)s.ProcessId, s.ServiceType, s.Win32ExitCode, s.ServiceSpecificExitCode));
                    }
                }

                if (done)
                {
                    return entries;
                }

                if (needed > buffer.Length)
                {
                    buffer = new byte[needed];
                }
            }
        }

        public ServiceDetail Detail(string name)
        {
            nint service = OpenOne(name, ServiceNative.ServiceQueryConfig | ServiceNative.ServiceQueryStatus);
            try
            {
                var config = Configuration(service, name);
                var status = StatusOf(service);
                try
                {
                    return new ServiceDetail(config, status, Dependents(name, activeOnly: false));
                }
                catch (ServiceException ex) when (ex.Code == ServiceNative.ErrorAccessDenied)
                {
                    return new ServiceDetail(config, status, [], DependentsKnown: false);
                }
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public ServiceStatus Status(string name)
        {
            nint service = OpenOne(name, ServiceNative.ServiceQueryStatus);
            try
            {
                return StatusOf(service);
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public (ServiceStartType Start, bool Delayed) StartOf(string name)
        {
            nint service = OpenOne(name, ServiceNative.ServiceQueryConfig);
            try
            {
                byte[] buffer = Query((p, size) => (ServiceNative.QueryServiceConfig(service, p, size, out uint needed), needed), "QueryServiceConfigW");
                ServiceStartType start;
                fixed (byte* p = buffer)
                {
                    start = (ServiceStartType)((ServiceNative.QueryServiceConfigNative*)p)->StartType;
                }

                bool delayed = false;
                if (start == ServiceStartType.Automatic && TryQuery2(service, ServiceNative.ServiceConfigDelayedAutoStartInfo) is { } info)
                {
                    fixed (byte* d = info)
                    {
                        delayed = *(int*)d != 0;
                    }
                }

                return (start, delayed);
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public IReadOnlyList<ServiceEntry> Dependents(string name, bool activeOnly)
        {
            nint service = OpenOne(name, ServiceNative.ServiceEnumerateDependents);
            try
            {
                return DependentsOf(service, activeOnly);
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public void Start(string name)
        {
            nint service = OpenOne(name, ServiceNative.ServiceStart);
            try
            {
                if (!ServiceNative.StartService(service, 0, null))
                {
                    throw Failure("StartServiceW");
                }
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public ServiceStatus Stop(string name)
        {
            nint service = OpenOne(name, ServiceNative.ServiceStop | ServiceNative.ServiceQueryStatus);
            try
            {
                ServiceNative.ServiceStatusNative status;
                if (!ServiceNative.ControlService(service, ServiceNative.ServiceControlStop, &status))
                {
                    throw Failure("ControlService");
                }

                return new ServiceStatus((ServiceRunState)status.CurrentState, 0, status.Win32ExitCode, status.ServiceSpecificExitCode, status.CheckPoint, status.WaitHint, status.ControlsAccepted);
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        public void SetStartType(string name, ServiceStartChoice choice)
        {
            nint service = OpenOne(name, ServiceNative.ServiceChangeConfig | ServiceNative.ServiceQueryConfig);
            try
            {
                uint startType = choice switch
                {
                    ServiceStartChoice.Automatic or ServiceStartChoice.Delayed => (uint)ServiceStartType.Automatic,
                    ServiceStartChoice.Manual => (uint)ServiceStartType.Manual,
                    _ => (uint)ServiceStartType.Disabled,
                };
                if (!ServiceNative.ChangeServiceConfig(service, ServiceNative.ServiceNoChange, startType, ServiceNative.ServiceNoChange, null, null, null, null, null, null, null))
                {
                    throw Failure("ChangeServiceConfigW");
                }

                if (choice is ServiceStartChoice.Automatic or ServiceStartChoice.Delayed)
                {
                    int delayed = choice == ServiceStartChoice.Delayed ? 1 : 0;
                    if (!ServiceNative.ChangeServiceConfig2(service, ServiceNative.ServiceConfigDelayedAutoStartInfo, &delayed))
                    {
                        // The start type is already automatic (2026-10-20, a review: this was answered and audited as a plain failure).
                        int code = Marshal.GetLastPInvokeError();
                        throw new ServiceException(code, "ChangeServiceConfig2W failed: " + Marshal.GetPInvokeErrorMessage(code)) { Applied = ServiceText.ChoiceWord(ServiceStartChoice.Automatic) };
                    }
                }
            }
            finally
            {
                ServiceNative.CloseServiceHandle(service);
            }
        }

        /// <summary>
        /// The service by its name. A name holding a NUL is refused here (2026-10-20, a review): OpenServiceW reads up to the first one, so
        /// "TermService\0x" would open TermService past every check made on the whole name. The door refuses such a name first; this is the floor.
        /// </summary>
        private nint OpenOne(string name, uint access)
        {
            if (name.Contains('\0', StringComparison.Ordinal))
            {
                throw new ServiceException(ServiceNative.ErrorInvalidName, "OpenServiceW: the name holds a NUL");
            }

            nint service = ServiceNative.OpenService(manager, name, access);
            return service != 0 ? service : throw Failure("OpenServiceW");
        }

        private static ServiceStatus StatusOf(nint service)
        {
            ServiceNative.ServiceStatusProcess status;
            if (!ServiceNative.QueryServiceStatusEx(service, ServiceNative.ScStatusProcessInfo, (byte*)&status, (uint)sizeof(ServiceNative.ServiceStatusProcess), out _))
            {
                throw Failure("QueryServiceStatusEx");
            }

            return new ServiceStatus((ServiceRunState)status.CurrentState, (int)status.ProcessId, status.Win32ExitCode, status.ServiceSpecificExitCode, status.CheckPoint, status.WaitHint, status.ControlsAccepted);
        }

        private static ServiceConfiguration Configuration(nint service, string name)
        {
            byte[] buffer = Query((p, size) => (ServiceNative.QueryServiceConfig(service, p, size, out uint needed), needed), "QueryServiceConfigW");
            fixed (byte* p = buffer)
            {
                var config = (ServiceNative.QueryServiceConfigNative*)p;
                string? description = null;
                bool delayed = false;
                ServiceFailureActions? failure = null;
                if (TryQuery2(service, ServiceNative.ServiceConfigDescription) is { } text)
                {
                    fixed (byte* d = text)
                    {
                        description = Optional(*(nint*)d);
                    }
                }

                if (TryQuery2(service, ServiceNative.ServiceConfigDelayedAutoStartInfo) is { } delayInfo)
                {
                    fixed (byte* d = delayInfo)
                    {
                        delayed = *(int*)d != 0;
                    }
                }

                if (TryQuery2(service, ServiceNative.ServiceConfigFailureActions) is { } failureInfo)
                {
                    fixed (byte* f = failureInfo)
                    {
                        var actions = (ServiceNative.ServiceFailureActionsNative*)f;
                        var list = new List<ServiceFailureAction>((int)actions->ActionCount);
                        var items = (ServiceNative.ScAction*)actions->Actions;
                        for (int i = 0; i < actions->ActionCount && items is not null; i++)
                        {
                            list.Add(new ServiceFailureAction(items[i].Type, items[i].Delay));
                        }

                        failure = new ServiceFailureActions(actions->ResetPeriod, Optional(actions->Command), Optional(actions->RebootMessage), list);
                    }
                }

                return new ServiceConfiguration(
                    name,
                    Text(config->DisplayName),
                    description,
                    config->ServiceType,
                    (ServiceStartType)config->StartType,
                    delayed,
                    Text(config->BinaryPathName),
                    Text(config->ServiceStartName),
                    Optional(config->LoadOrderGroup),
                    MultiString(config->Dependencies),
                    failure);
            }
        }

        /// <summary>A QueryServiceConfig2W level's buffer, or null when the service has none to give (an older service, no rights).</summary>
        private static byte[]? TryQuery2(nint service, uint level)
        {
            try
            {
                return Query((p, size) => (ServiceNative.QueryServiceConfig2(service, level, p, size, out uint needed), needed), "QueryServiceConfig2W");
            }
            catch (ServiceException)
            {
                return null;
            }
        }

        private delegate (bool Ok, uint Needed) Call(byte* buffer, uint size);

        /// <summary>The ask-twice dance: an 8 KB buffer, then the size the call said it needs.</summary>
        private static byte[] Query(Call call, string name)
        {
            byte[] buffer = new byte[8 * 1024];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                fixed (byte* p = buffer)
                {
                    var (ok, needed) = call(p, (uint)buffer.Length);
                    if (ok)
                    {
                        return buffer;
                    }

                    int error = Marshal.GetLastPInvokeError();
                    if (error != ServiceNative.ErrorInsufficientBuffer || needed <= buffer.Length)
                    {
                        throw new ServiceException(error, name + " failed: " + Marshal.GetPInvokeErrorMessage(error));
                    }

                    buffer = new byte[needed];
                }
            }

            throw new ServiceException(ServiceNative.ErrorInsufficientBuffer, name + " kept growing");
        }

        private static IReadOnlyList<ServiceEntry> DependentsOf(nint service, bool activeOnly)
        {
            byte[] buffer = new byte[16 * 1024];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                fixed (byte* p = buffer)
                {
                    if (ServiceNative.EnumDependentServices(service, activeOnly ? ServiceNative.ServiceStateActive : ServiceNative.ServiceStateAll, p, (uint)buffer.Length, out uint needed, out uint returned))
                    {
                        var items = (ServiceNative.EnumServiceStatus*)p;
                        var list = new List<ServiceEntry>((int)returned);
                        for (int i = 0; i < returned; i++)
                        {
                            var s = items[i].Status;
                            list.Add(new ServiceEntry(Text(items[i].ServiceName), Text(items[i].DisplayName), (ServiceRunState)s.CurrentState, 0, s.ServiceType, s.Win32ExitCode, s.ServiceSpecificExitCode));
                        }

                        return list;
                    }

                    int error = Marshal.GetLastPInvokeError();
                    if (error != ServiceNative.ErrorMoreData || needed <= buffer.Length)
                    {
                        throw new ServiceException(error, "EnumDependentServicesW failed: " + Marshal.GetPInvokeErrorMessage(error));
                    }

                    buffer = new byte[needed];
                }
            }

            throw new ServiceException(ServiceNative.ErrorMoreData, "EnumDependentServicesW kept growing");
        }

        private static string Text(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUni(pointer) ?? "";

        private static string? Optional(nint pointer) => pointer == 0 ? null : Marshal.PtrToStringUni(pointer) is { Length: > 0 } s ? s : null;

        /// <summary>A double-null-terminated list (the dependencies), a group's <c>+</c> kept.</summary>
        private static IReadOnlyList<string> MultiString(nint pointer)
        {
            var list = new List<string>();
            if (pointer == 0)
            {
                return list;
            }

            var p = (char*)pointer;
            while (*p != '\0')
            {
                string item = new(p);
                list.Add(item);
                p += item.Length + 1;
            }

            return list;
        }
    }
}
