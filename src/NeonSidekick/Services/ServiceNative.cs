using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Services;

/// <summary>
/// advapi32's Service Control Manager entry points (2026-10-15, the admin-tools plan): <c>LibraryImport</c> over blittable arguments, every
/// buffer a byte array the caller reads by offset (<see cref="WindowsServiceSystem"/>), so nothing marshals by reflection. The rights asked for
/// are always named — never <c>MAXIMUM_ALLOWED</c> (the CVE-2021-27086 note). A system library: no DLL ships. Excluded from coverage: proven by
/// the smoke's <c>services:scm</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ServiceNative
{
    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerEnumerateService = 0x0004;

    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceEnumerateDependents = 0x0008;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;

    public const uint ServiceStateActive = 0x1;
    public const uint ServiceStateAll = 0x3;
    public const int ScEnumProcessInfo = 0;
    public const int ScStatusProcessInfo = 0;

    public const uint ServiceConfigDescription = 1;
    public const uint ServiceConfigFailureActions = 2;
    public const uint ServiceConfigDelayedAutoStartInfo = 3;

    public const uint ServiceControlStop = 1;
    public const uint ServiceNoChange = 0xFFFFFFFF;

    public const int ErrorAccessDenied = 5;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorInvalidName = 123;
    public const int ErrorMoreData = 234;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint manager, string serviceName, uint desiredAccess);

    /// <summary>A service's key name from its display name (2026-10-23, the admin review's N5: the canonical name the checks and the audit use).</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "GetServiceKeyNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetServiceKeyName(nint manager, string displayName, char* serviceName, ref uint length);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumServicesStatusEx(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryServiceConfig(nint service, byte* config, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryServiceConfig2(nint service, uint infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryServiceStatusEx(nint service, int infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool EnumDependentServices(nint service, uint serviceState, byte* services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool StartService(nint service, uint argumentCount, char** arguments);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ControlService(nint service, uint control, ServiceStatusNative* status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, char* binaryPath, char* loadOrderGroup, uint* tagId, char* dependencies, char* startName, char* password, char* displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ChangeServiceConfig2(nint service, uint infoLevel, void* info);

    /// <summary>SERVICE_STATUS: seven DWORDs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusNative
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    /// <summary>SERVICE_STATUS_PROCESS: SERVICE_STATUS, then the process id and the flags.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    /// <summary>ENUM_SERVICE_STATUS_PROCESSW: two string pointers and a SERVICE_STATUS_PROCESS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatusProcess
    {
        public nint ServiceName;
        public nint DisplayName;
        public ServiceStatusProcess Status;
    }

    /// <summary>ENUM_SERVICE_STATUSW: two string pointers and a SERVICE_STATUS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EnumServiceStatus
    {
        public nint ServiceName;
        public nint DisplayName;
        public ServiceStatusNative Status;
    }

    /// <summary>QUERY_SERVICE_CONFIGW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct QueryServiceConfigNative
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    /// <summary>SERVICE_FAILURE_ACTIONSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceFailureActionsNative
    {
        public uint ResetPeriod;
        public nint RebootMessage;
        public nint Command;
        public uint ActionCount;
        public nint Actions;
    }

    /// <summary>SC_ACTION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ScAction
    {
        public int Type;
        public uint Delay;
    }
}
