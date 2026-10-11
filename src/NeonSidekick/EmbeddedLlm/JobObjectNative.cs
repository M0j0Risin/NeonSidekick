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

    // 2026-10-11 (code review of the headless browser): a start inside the job from the first instruction — CreateProcessW
    // suspended with an extended STARTUPINFO whose attribute list names the only handles the child inherits, the job assigned,
    // then the thread resumed (ChildJob.Start). Process.Start can do neither.

    /// <summary><c>CREATE_SUSPENDED</c>.</summary>
    internal const uint CreateSuspended = 0x4;

    /// <summary><c>CREATE_NO_WINDOW</c>.</summary>
    internal const uint CreateNoWindow = 0x08000000;

    /// <summary><c>EXTENDED_STARTUPINFO_PRESENT</c>.</summary>
    internal const uint ExtendedStartupInfoPresent = 0x80000;

    /// <summary><c>STARTF_USESTDHANDLES</c>.</summary>
    internal const uint StartfUseStdHandles = 0x100;

    /// <summary><c>HANDLE_FLAG_INHERIT</c>.</summary>
    internal const uint HandleFlagInherit = 0x1;

    /// <summary><c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>.</summary>
    internal const nint ProcThreadAttributeHandleList = 0x20002;

    /// <summary><c>STARTUPINFOW</c> (104 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    /// <summary><c>STARTUPINFOEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    /// <summary><c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcess(string applicationName, char* commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, uint flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nint attribute, nint value, nint size, nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    /// <summary>The thread's previous suspend count, or <c>0xFFFFFFFF</c> on failure.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);
}
