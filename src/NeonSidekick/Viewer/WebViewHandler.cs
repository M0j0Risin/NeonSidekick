using System.Runtime.InteropServices;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>
/// The COM objects WebView2 calls back (2026-10-05, the video window): the codebase's first app-made COM objects. Until now
/// every COM object was someone else's, called through its vtable (the camera, the GPU meters, the shell's drag data object);
/// WebView2 is asynchronous all the way down and takes a handler object for every completion and every event, so here the
/// app is on the other side. Not <c>ComWrappers</c>: a managed class wrapped for COM brings an interface table and a
/// dispatch layer for what is three pointers and a count, and the raw block is what <c>[UnmanagedCallersOnly]</c> was made for.
///
/// <para>An object is a native block — vtable pointer, reference count, a <see cref="GCHandle"/> to the managed callback, the
/// IID it answers to — that WebView2 may hold past any managed lifetime. QueryInterface answers IUnknown and that IID;
/// the last Release frees the handle and the block. A new object starts at one reference, the caller's: hand it to WebView2
/// (which takes its own) and <see cref="Release"/> it. The three Invoke signatures are three vtables, built once for the
/// process, never freed: an event handler's <c>(sender, args)</c> pointers, a completion's <c>(HRESULT, result)</c> where the
/// HRESULT is 32 bits (read as a pointer it would carry the register's garbage upper half), and ExecuteScript's
/// <c>(HRESULT, LPCWSTR json)</c>, whose string is WebView2's and is copied, never freed.</para>
///
/// <para>A callback that throws is logged and answered S_OK, the window procedures' rule: nothing may unwind into native
/// code, which would take the process down.</para>
/// </summary>
internal static unsafe class WebViewHandler
{
    internal const int SOk = 0;
    internal const int ENoInterface = unchecked((int)0x80004002);

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private struct Block
    {
        public nint* Vtbl;
        public int References;
        public nint Target;
        public Guid Iid;
    }

    private static readonly nint* s_event = Table((nint)(delegate* unmanaged<Block*, nint, nint, int>)&InvokeEvent);
    private static readonly nint* s_completed = Table((nint)(delegate* unmanaged<Block*, int, nint, int>)&InvokeCompleted);
    private static readonly nint* s_script = Table((nint)(delegate* unmanaged<Block*, int, char*, int>)&InvokeScript);

    /// <summary>An event handler (<c>Invoke(sender, args)</c>) answering <paramref name="iid"/>.</summary>
    public static nint Event(Guid iid, Action<nint, nint> invoke) => Create(s_event, iid, invoke);

    /// <summary>A completion handler (<c>Invoke(HRESULT, result)</c>) answering <paramref name="iid"/>.</summary>
    public static nint Completed(Guid iid, Action<int, nint> invoke) => Create(s_completed, iid, invoke);

    /// <summary>ExecuteScript's completion handler (<c>Invoke(HRESULT, json)</c>), the result copied (null when there is none).</summary>
    public static nint Script(Guid iid, Action<int, string?> invoke) => Create(s_script, iid, invoke);

    /// <summary>Drops one reference, the caller's after handing the object over.</summary>
    public static uint Release(nint handler) => handler == 0 ? 0 : ReleaseCore((Block*)handler);

    private static nint* Table(nint invoke)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
        table[0] = (nint)(delegate* unmanaged<Block*, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<Block*, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<Block*, uint>)&ComRelease;
        table[3] = invoke;
        return table;
    }

    private static nint Create(nint* vtbl, Guid iid, object target)
    {
        var block = (Block*)NativeMemory.Alloc((nuint)sizeof(Block));
        block->Vtbl = vtbl;
        block->References = 1;
        block->Target = GCHandle.ToIntPtr(GCHandle.Alloc(target));
        block->Iid = iid;
        return (nint)block;
    }

    private static uint ReleaseCore(Block* self)
    {
        int left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            GCHandle.FromIntPtr(self->Target).Free();
            NativeMemory.Free(self);
        }

        return (uint)left;
    }

    private static T Target<T>(Block* self) => (T)GCHandle.FromIntPtr(self->Target).Target!;

    [UnmanagedCallersOnly]
    private static int QueryInterface(Block* self, Guid* iid, nint* found)
    {
        if (*iid == IidUnknown || *iid == self->Iid)
        {
            Interlocked.Increment(ref self->References);
            *found = (nint)self;
            return SOk;
        }

        *found = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(Block* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly]
    private static uint ComRelease(Block* self) => ReleaseCore(self);

    [UnmanagedCallersOnly]
    private static int InvokeEvent(Block* self, nint sender, nint args)
    {
        try
        {
            Target<Action<nint, nint>>(self)(sender, args);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A WebView2 event handler failed.", ex);
        }

        return SOk;
    }

    [UnmanagedCallersOnly]
    private static int InvokeCompleted(Block* self, int hr, nint result)
    {
        try
        {
            Target<Action<int, nint>>(self)(hr, result);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A WebView2 completion handler failed.", ex);
        }

        return SOk;
    }

    [UnmanagedCallersOnly]
    private static int InvokeScript(Block* self, int hr, char* json)
    {
        try
        {
            Target<Action<int, string?>>(self)(hr, json is null ? null : new string(json));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A WebView2 script handler failed.", ex);
        }

        return SOk;
    }
}
