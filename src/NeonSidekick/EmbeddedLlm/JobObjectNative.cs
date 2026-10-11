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

    // 2026-10-11 (code review): the process snapshot ChildJob.AssignTree reads for a child started before its parent joined the job.

    /// <summary><c>TH32CS_SNAPPROCESS</c>.</summary>
    internal const uint SnapProcess = 0x2;

    /// <summary><c>INVALID_HANDLE_VALUE</c>, what a failed snapshot returns.</summary>
    internal const nint InvalidHandle = -1;

    /// <summary><c>PROCESSENTRY32W</c>, the name as UTF-16 units so the struct stays blittable (568 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed ushort ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
