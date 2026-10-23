using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Tasks;

/// <summary>
/// The Task Scheduler 2.0 COM API by raw vtables (2026-10-15, the admin-tools plan, <c>ShortcutNative</c>'s shape): <c>CoCreateInstance</c> of
/// the TaskScheduler coclass, then every call through its interface's vtable with unmanaged function pointers — no <c>ComWrappers</c>, nothing
/// for AOT to marshal. Each interface is dual (it derives from IDispatch), so its own methods start at slot 7. Slots checked against Wine's
/// <c>include/taskschd.idl</c> (2026-10-15 web pass); Wine's IDL is a reimplementation, so the smoke's <c>tasks:com</c> on real Windows is the
/// proof of each slot used. Strings are BSTRs (<see cref="Marshal.StringToBSTR"/>, freed after; the password zeroed), VARIANTs our own
/// 24-byte <see cref="Variant"/> (VT_EMPTY zeroed, VT_I4 for a 1-based index, VT_BSTR), VARIANT_BOOL −1/0, DATE through
/// <see cref="DateTime.FromOADate"/>. Writes use only IRegisteredTask's Run, Stop and put_Enabled — never RegisterTaskDefinition. Never
/// <c>CoInitializeSecurity</c> (process-wide, and WebView2 and the camera share the process); a remote access-denied would be answered by
/// <c>CoSetProxyBlanket</c> on the service's proxy alone (<c>.notes/remote_admin_todo.md</c>). Excluded from coverage.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class TaskNative
{
    internal const uint CoinitMultithreaded = 0x0;
    internal const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint ClsctxInprocServer = 0x1;

    internal const int TaskEnumHidden = 1;

    private static readonly Guid ClsidTaskScheduler = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");
    private static readonly Guid IidTaskService = new("2faba4c7-4da9-4013-9697-20cc3fd40f85");

    // IUnknown
    private const int ReleaseSlot = 2;

    // ITaskService
    internal const int ServiceGetFolder = 7;
    internal const int ServiceConnect = 10;
    internal const int ServiceTargetServer = 12;
    internal const int ServiceConnectedUser = 13;
    internal const int ServiceConnectedDomain = 14;
    internal const int ServiceHighestVersion = 15;

    // ITaskFolder
    internal const int FolderPath = 8;
    internal const int FolderGetFolders = 10;
    internal const int FolderGetTask = 13;
    internal const int FolderGetTasks = 14;

    // The three collections
    internal const int CollectionCount = 7;
    internal const int CollectionItem = 8;

    // IRegisteredTask
    internal const int TaskName = 7;
    internal const int TaskPath = 8;
    internal const int TaskState = 9;
    internal const int TaskGetEnabled = 10;
    internal const int TaskPutEnabled = 11;
    internal const int TaskRun = 12;
    internal const int TaskGetInstances = 14;
    internal const int TaskLastRunTime = 15;
    internal const int TaskLastTaskResult = 16;
    internal const int TaskMissedRuns = 17;
    internal const int TaskNextRunTime = 18;
    internal const int TaskXml = 20;
    internal const int TaskStop = 23;

    // IRunningTask
    internal const int RunningInstanceGuid = 8;
    internal const int RunningState = 10;
    internal const int RunningCurrentAction = 11;
    internal const int RunningEnginePid = 14;

    /// <summary>A VARIANT: the type at 0, the value from 8, 24 bytes in all.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct Variant
    {
        public const ushort Empty = 0;
        public const ushort I4 = 3;
        public const ushort Bstr = 8;

        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public nint Pointer;

        [FieldOffset(8)]
        public int Int;

        public static Variant Of(int value) => new() { Type = I4, Int = value };

        public static Variant Of(nint bstr) => bstr == 0 ? default : new Variant { Type = Bstr, Pointer = bstr };
    }

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    /// <summary>A new ITaskService, not yet connected.</summary>
    internal static nint CreateService()
    {
        Guid clsid = ClsidTaskScheduler;
        Guid iid = IidTaskService;
        nint service;
        Check(CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &service), "CoCreateInstance(TaskScheduler)");
        return service;
    }

    /// <summary>ITaskService::Connect: the server, the user, the domain and the password as VARIANTs (VT_EMPTY for the defaults).</summary>
    internal static void Connect(nint service, string? server, string? user, string? domain, string? password)
    {
        nint s = server is null ? 0 : Marshal.StringToBSTR(server);
        nint u = user is null ? 0 : Marshal.StringToBSTR(user);
        nint d = domain is null ? 0 : Marshal.StringToBSTR(domain);
        nint p = password is null ? 0 : Marshal.StringToBSTR(password);
        try
        {
            Check(((delegate* unmanaged<nint, Variant, Variant, Variant, Variant, int>)Slot(service, ServiceConnect))(service, Variant.Of(s), Variant.Of(u), Variant.Of(d), Variant.Of(p)), "ITaskService.Connect");
        }
        finally
        {
            Marshal.FreeBSTR(s);
            Marshal.FreeBSTR(u);
            Marshal.FreeBSTR(d);
            if (p != 0)
            {
                Marshal.ZeroFreeBSTR(p);
            }
        }
    }

    /// <summary>A BSTR property read and freed.</summary>
    internal static string GetString(nint instance, int slot, string call)
    {
        nint bstr = 0;
        Check(((delegate* unmanaged<nint, nint*, int>)Slot(instance, slot))(instance, &bstr), call);
        try
        {
            return bstr == 0 ? "" : Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    /// <summary>A LONG, DWORD or enum property.</summary>
    internal static int GetInt(nint instance, int slot, string call)
    {
        int value = 0;
        Check(((delegate* unmanaged<nint, int*, int>)Slot(instance, slot))(instance, &value), call);
        return value;
    }

    /// <summary>A VARIANT_BOOL property.</summary>
    internal static bool GetBool(nint instance, int slot, string call)
    {
        short value = 0;
        Check(((delegate* unmanaged<nint, short*, int>)Slot(instance, slot))(instance, &value), call);
        return value != 0;
    }

    /// <summary>
    /// A DATE property; null for never — the zero DATE, or the Task Scheduler's own "never", 1999-11-30 (found on this PC, 2026-10-15: a task
    /// that has not run reports its last run so), so any date before 2000.
    /// </summary>
    internal static DateTime? GetDate(nint instance, int slot, string call)
    {
        double value = 0;
        Check(((delegate* unmanaged<nint, double*, int>)Slot(instance, slot))(instance, &value), call);
        return TaskWords.Date(value);
    }

    /// <summary>A method taking a BSTR and giving an interface: GetFolder, GetTask.</summary>
    internal static nint GetByPath(nint instance, int slot, string path, string call)
    {
        nint bstr = Marshal.StringToBSTR(path);
        try
        {
            nint result;
            Check(((delegate* unmanaged<nint, nint, nint*, int>)Slot(instance, slot))(instance, bstr, &result), call);
            return result;
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    /// <summary>A method taking a LONG of flags and giving an interface: GetFolders, GetTasks, GetInstances.</summary>
    internal static nint GetByFlags(nint instance, int slot, int flags, string call)
    {
        nint result;
        Check(((delegate* unmanaged<nint, int, nint*, int>)Slot(instance, slot))(instance, flags, &result), call);
        return result;
    }

    /// <summary>A collection's item by its 1-based index.</summary>
    internal static nint Item(nint collection, int index, string call)
    {
        nint result;
        Check(((delegate* unmanaged<nint, Variant, nint*, int>)Slot(collection, CollectionItem))(collection, Variant.Of(index), &result), call);
        return result;
    }

    /// <summary>IRegisteredTask::put_Enabled.</summary>
    internal static void PutEnabled(nint task, bool enabled) =>
        Check(((delegate* unmanaged<nint, short, int>)Slot(task, TaskPutEnabled))(task, enabled ? (short)-1 : (short)0), "IRegisteredTask.put_Enabled");

    /// <summary>IRegisteredTask::Run with no parameters (VT_EMPTY); the running task it gives is released at once.</summary>
    internal static void Run(nint task)
    {
        nint running = 0;
        Check(((delegate* unmanaged<nint, Variant, nint*, int>)Slot(task, TaskRun))(task, default, &running), "IRegisteredTask.Run");
        Release(running);
    }

    /// <summary>IRegisteredTask::Stop(0): every instance.</summary>
    internal static void Stop(nint task) => Check(((delegate* unmanaged<nint, int, int>)Slot(task, TaskStop))(task, 0), "IRegisteredTask.Stop");

    internal static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new TaskException(hr, $"{call} failed (0x{hr:X8})");
        }
    }

    internal static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, ReleaseSlot))(unknown);
        }
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
}
