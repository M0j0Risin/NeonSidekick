using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The kernel32 job-object calls <see cref="ChildJob"/> needs (2026-09-29): a job whose last handle closing kills every
/// process in it (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>). Source-generated <see cref="LibraryImportAttribute"/>
/// stubs over blittable structs passed by reference — nothing for AOT to marshal by value. Windows-only, like the
/// other layers the portability note lists; <see cref="ChildJob"/> is the guarded way in.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class JobObjectNative
{
    /// <summary><c>JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation</c>.</summary>
    internal const int JobObjectExtendedLimitInformation = 9;

    internal const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
