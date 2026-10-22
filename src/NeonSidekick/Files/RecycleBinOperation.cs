using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// The move to Windows' Recycle bin through <c>IFileOperation</c> (2026-10-22, review J1, the fix plan's mechanism 5 and D11, the user's call:
/// refuse instead of nuking). <c>SHFileOperationW</c> with <c>FOF_ALLOWUNDO</c> deleted an item the bin could not take — too big for it, or a
/// bin set to remove at once — for good, answered 0 and was reported "moved to the Recycle bin". Here a progress sink of the app's own is handed
/// to <c>DeleteItem</c>: its <c>PreDeleteItem</c> sees <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c> when the item will go to the bin and answers
/// <c>E_ABORT</c> when it won't, so such an item is not deleted at all and the caller says so (Electron's MoveItemToTrash does the same).
/// <para>Raw vtables (the Task Scheduler's way, <c>Tasks/TaskNative</c>): <c>IFileOperation</c> made by <c>CoCreateInstance</c>, the item by
/// <c>SHCreateItemFromParsingName</c>; the sink a native block with an <c>[UnmanagedCallersOnly]</c> vtable (<c>Viewer/WebViewHandler</c>'s
/// shape), AOT-safe. shell32 and ole32 are loaded already; no new library. Run on <see cref="RecycleBin.RunCapped"/>'s STA thread. Excluded
/// from coverage with the native layers; smoke <c>files:recycle-bin</c>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class RecycleBinOperation
{
    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IidFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidSink = new("04b0f1a7-9490-44bc-96e1-4296a31252e2");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private const int SOk = 0;
    private const int EAbort = unchecked((int)0x80004004);
    private const int ENoInterface = unchecked((int)0x80004002);
    private const uint ClsctxAll = 0x17;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoUi = 0x0614;   // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
    private const uint FofxRecycleOnDelete = 0x0008_0000;
    private const uint FofxEarlyFailure = 0x0010_0000;
    private const uint TsfDeleteRecycleIfPossible = 0x80;

    // IFileOperation's slots after IUnknown's three.
    private const int SetOperationFlags = 5;
    private const int DeleteItem = 18;
    private const int PerformOperations = 21;
    private const int GetAnyOperationsAborted = 22;

    /// <summary>The sink's state, read once the operation ends.</summary>
    private sealed class SinkState
    {
        public bool NotRecyclable;
        public int DeleteResult;
    }

    private struct SinkBlock
    {
        public nint* Vtbl;
        public int References;
        public nint State;
    }

    private static readonly nint* s_sinkTable = SinkTable();

    /// <summary>
    /// <paramref name="fullPath"/> moved to the bin: the operation's HRESULT, whether it was aborted, and whether the bin could not take the
    /// item (left in place, never deleted). The smoke's probe runs it on a path that does not exist.
    /// </summary>
    public static (int Code, bool Aborted, bool NotRecyclable) Recycle(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        Guid clsid = ClsidFileOperation, iidOperation = IidFileOperation, iidItem = IidShellItem;
        int hr = CoCreateInstance(&clsid, 0, ClsctxAll, &iidOperation, out nint operation);
        if (hr < 0)
        {
            return (hr, false, false);
        }

        nint item = 0;
        var state = new SinkState();
        var handle = GCHandle.Alloc(state);
        var sink = (SinkBlock*)NativeMemory.Alloc((nuint)sizeof(SinkBlock));
        sink->Vtbl = s_sinkTable;
        sink->References = 1;
        sink->State = GCHandle.ToIntPtr(handle);
        try
        {
            var vtbl = *(nint**)operation;
            hr = ((delegate* unmanaged<nint, uint, int>)vtbl[SetOperationFlags])(operation, FofAllowUndo | FofNoUi | FofxRecycleOnDelete | FofxEarlyFailure);
            if (hr < 0)
            {
                return (hr, false, false);
            }

            hr = SHCreateItemFromParsingName(fullPath, 0, &iidItem, out item);
            if (hr < 0)
            {
                return (hr, false, false);
            }

            hr = ((delegate* unmanaged<nint, nint, nint, int>)vtbl[DeleteItem])(operation, item, (nint)sink);
            if (hr < 0)
            {
                return (hr, false, false);
            }

            hr = ((delegate* unmanaged<nint, int>)vtbl[PerformOperations])(operation);
            int aborted = 0;
            _ = ((delegate* unmanaged<nint, int*, int>)vtbl[GetAnyOperationsAborted])(operation, &aborted);
            if (hr >= 0 && state.DeleteResult < 0)
            {
                hr = state.DeleteResult;
            }

            return (hr, aborted != 0, state.NotRecyclable);
        }
        finally
        {
            if (item != 0)
            {
                Release(item);
            }

            Release(operation);
            SinkRelease(sink);
        }
    }

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    private static nint* SinkTable()
    {
        // IUnknown's three, then the sink's sixteen: StartOperations, FinishOperations, PreRename, PostRename, PreMove, PostMove, PreCopy,
        // PostCopy, PreDelete, PostDelete, PreNew, PostNew, UpdateProgress, ResetTimer, PauseTimer, ResumeTimer.
        var table = (nint*)NativeMemory.Alloc((nuint)(19 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged<SinkBlock*, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<SinkBlock*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<SinkBlock*, uint>)&ComRelease;
        table[3] = (nint)(delegate* unmanaged<SinkBlock*, int>)&Ok0;                                  // StartOperations
        table[4] = (nint)(delegate* unmanaged<SinkBlock*, int, int>)&Ok1;                             // FinishOperations(HRESULT)
        table[5] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, int>)&Ok3;                // PreRenameItem
        table[6] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, int, nint, int>)&Ok5;     // PostRenameItem
        table[7] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, nint, int>)&Ok4;          // PreMoveItem
        table[8] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, nint, int, nint, int>)&Ok6; // PostMoveItem
        table[9] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, nint, int>)&Ok4;          // PreCopyItem
        table[10] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, nint, int, nint, int>)&Ok6; // PostCopyItem
        table[11] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, int>)&PreDeleteItem;
        table[12] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, int, nint, int>)&PostDeleteItem;
        table[13] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, int>)&Ok3;               // PreNewItem
        table[14] = (nint)(delegate* unmanaged<SinkBlock*, uint, nint, nint, nint, uint, int, nint, int>)&Ok7; // PostNewItem
        table[15] = (nint)(delegate* unmanaged<SinkBlock*, uint, uint, int>)&Ok2;                     // UpdateProgress
        table[16] = (nint)(delegate* unmanaged<SinkBlock*, int>)&Ok0;                                 // ResetTimer
        table[17] = (nint)(delegate* unmanaged<SinkBlock*, int>)&Ok0;                                 // PauseTimer
        table[18] = (nint)(delegate* unmanaged<SinkBlock*, int>)&Ok0;                                 // ResumeTimer
        return table;
    }

    private static SinkState StateOf(SinkBlock* self) => (SinkState)GCHandle.FromIntPtr(self->State).Target!;

    private static void SinkRelease(SinkBlock* self)
    {
        if (Interlocked.Decrement(ref self->References) == 0)
        {
            GCHandle.FromIntPtr(self->State).Free();
            NativeMemory.Free(self);
        }
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(SinkBlock* self, Guid* iid, nint* found)
    {
        if (*iid == IidUnknown || *iid == IidSink)
        {
            Interlocked.Increment(ref self->References);
            *found = (nint)self;
            return SOk;
        }

        *found = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(SinkBlock* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly]
    private static uint ComRelease(SinkBlock* self)
    {
        int left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(self->State).Free();
            NativeMemory.Free(self);
        }

        return (uint)left;
    }

    /// <summary>The bin's question before each item: abort one it would delete for good (no <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c>).</summary>
    [UnmanagedCallersOnly]
    private static int PreDeleteItem(SinkBlock* self, uint flags, nint item)
    {
        if ((flags & TsfDeleteRecycleIfPossible) != 0)
        {
            return SOk;
        }

        try
        {
            StateOf(self).NotRecyclable = true;
        }
        catch (InvalidOperationException)
        {
        }

        return EAbort;
    }

    [UnmanagedCallersOnly]
    private static int PostDeleteItem(SinkBlock* self, uint flags, nint item, int result, nint created)
    {
        try
        {
            if (result < 0)
            {
                StateOf(self).DeleteResult = result;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return SOk;
    }

    [UnmanagedCallersOnly]
    private static int Ok0(SinkBlock* self) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok1(SinkBlock* self, int a) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok2(SinkBlock* self, uint a, uint b) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok3(SinkBlock* self, uint a, nint b, nint c) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok4(SinkBlock* self, uint a, nint b, nint c, nint d) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok5(SinkBlock* self, uint a, nint b, nint c, int d, nint e) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok6(SinkBlock* self, uint a, nint b, nint c, nint d, int e, nint f) => SOk;

    [UnmanagedCallersOnly]
    private static int Ok7(SinkBlock* self, uint a, nint b, nint c, nint d, uint e, int f, nint g) => SOk;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint created);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, Guid* iid, out nint item);
}
