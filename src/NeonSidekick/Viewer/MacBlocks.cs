using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>
/// Blocks for the Mac's Browser window (2026-10-18, the Mac Browser plan): WebKit and AppKit take completion blocks and call them on the main
/// queue, so nothing there may wait for one (the screen capture's blocking <c>Await</c> would deadlock the main thread). Two kinds.
/// <list type="bullet">
/// <item>The app's own, handed out (<see cref="Once"/>): a global block in the camera's and screen's <c>BlockLiteral</c> layout whose invoke
/// runs a managed callback with the raw arguments, once, on whatever thread calls it; then the block is spent, kept, and reused after a
/// minute idle (the screen's rule: a late second call never finds another's callback in it). A block never handed over goes back with
/// <see cref="Drop"/>.</item>
/// <item>WebKit's, handed in (<see cref="HeldHandler"/>): a decision answered in the same call is answered in a <c>finally</c> with the
/// <c>Call</c> shapes; one answered later (a dialog's sheet, the open panel, the sign-in, a page's print) is <c>_Block_copy</c>'d and
/// answered exactly once, with a safe default when the window closes first. Phase 0 found WebKit throws (<c>NSInternalInconsistencyException</c>,
/// "Completion handler … was not called") when a handler it gave is released uncalled, and an Objective-C exception ends a NativeAOT process.</item>
/// </list>
/// Excluded from coverage with the AppKit layer; proven by the smoke's <c>browser:webkit</c>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacBlocks
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int BlockIsGlobal = 1 << 28;
    private static readonly TimeSpan Reuse = TimeSpan.FromMinutes(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
        public nint State;                           // ours: the GCHandle of the callback, 0 once called
        public BlockDescriptor OwnDescriptor;        // ours: the descriptor kept beside the block
    }

    private static readonly Lock s_gate = new();
    private static readonly Queue<(nint Block, long At)> s_spent = new();
    private static nint s_globalBlockClass;

    [LibraryImport(LibSystem)]
    private static partial nint _Block_copy(nint block);

    [LibraryImport(LibSystem)]
    private static partial void _Block_release(nint block);

    /// <summary>
    /// A block that runs <paramref name="callback"/> once with the arguments it is called with (each in an integer register: an object, an
    /// NSInteger, a BOOL in the low byte). The callback must not throw; one that does is logged.
    /// </summary>
    public static nint Once(Action<nint, nint, nint> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        BlockLiteral* block = null;
        lock (s_gate)
        {
            if (s_spent.TryPeek(out var spent) && Environment.TickCount64 - spent.At > (long)Reuse.TotalMilliseconds)
            {
                block = (BlockLiteral*)s_spent.Dequeue().Block;
            }

            if (s_globalBlockClass == 0)
            {
                s_globalBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock");
            }
        }

        if (block is null)
        {
            block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        }

        block->Isa = s_globalBlockClass;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = (nint)(delegate* unmanaged<BlockLiteral*, nint, nint, nint, void>)&Invoked;
        block->OwnDescriptor = new BlockDescriptor { Reserved = 0, Size = (nuint)sizeof(BlockLiteral) };
        block->Descriptor = &block->OwnDescriptor;
        block->State = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        return (nint)block;
    }

    /// <summary>A block of <see cref="Once"/>'s that was never handed over (the call that would have taken it failed): its callback let go.</summary>
    public static void Drop(nint block)
    {
        var literal = (BlockLiteral*)block;
        if (literal is null || literal->State == 0)
        {
            return;
        }

        GCHandle.FromIntPtr(literal->State).Free();
        literal->State = 0;
        Spent(block);
    }

    [UnmanagedCallersOnly]
    private static void Invoked(BlockLiteral* block, nint a, nint b, nint c)
    {
        try
        {
            nint state = Interlocked.Exchange(ref block->State, 0);
            if (state == 0)
            {
                return;   // called twice: the first call had it
            }

            var handle = GCHandle.FromIntPtr(state);
            var callback = (Action<nint, nint, nint>)handle.Target!;
            handle.Free();
            Spent((nint)block);
            callback(a, b, c);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing may leave an unmanaged callback.
            DiagnosticLog.Error("Browser", "A WebKit completion failed.", ex);
        }
    }

    private static void Spent(nint block)
    {
        lock (s_gate)
        {
            s_spent.Enqueue((block, Environment.TickCount64));
        }
    }

    // ---- WebKit's blocks: the function at offset 16 takes the block first ----

    public static void Call(nint block)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, void>)*(nint*)(block + 16))(block);
        }
    }

    public static void Call(nint block, nint a)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, nint, void>)*(nint*)(block + 16))(block, a);
        }
    }

    public static void CallLong(nint block, long a)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, long, void>)*(nint*)(block + 16))(block, a);
        }
    }

    public static void CallBool(nint block, bool a)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, byte, void>)*(nint*)(block + 16))(block, (byte)(a ? 1 : 0));
        }
    }

    /// <summary>An auth challenge's answer: the disposition and the credential (nil for none).</summary>
    public static void CallLongObject(nint block, long a, nint b)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, long, nint, void>)*(nint*)(block + 16))(block, a, b);
        }
    }

    public static nint Copy(nint block) => block == 0 ? 0 : _Block_copy(block);

    public static void Release(nint block)
    {
        if (block != 0)
        {
            _Block_release(block);
        }
    }
}

/// <summary>
/// A completion handler WebKit handed in and the window answers later (<see cref="MacBlocks"/>): copied as it is taken, answered exactly once —
/// by <see cref="Answer"/>, or with its safe default (<see cref="Fallback"/>: cancel, false, nil) when the window closes first — and released
/// after. Main thread only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class HeldHandler
{
    private nint _block;
    private readonly Action<nint> _fallback;

    public HeldHandler(nint block, Action<nint> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        _block = MacBlocks.Copy(block);
        _fallback = fallback;
    }

    /// <summary>Whether it still waits for its answer.</summary>
    public bool Pending => _block != 0;

    /// <summary>The answer given through <paramref name="call"/> (one of <see cref="MacBlocks"/>' <c>Call</c> shapes); false when already answered.</summary>
    public bool Answer(Action<nint> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        nint block = _block;
        if (block == 0)
        {
            return false;
        }

        _block = 0;
        try
        {
            call(block);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Error("Browser", "A page's handler could not be answered.", ex);
        }
        finally
        {
            MacBlocks.Release(block);
        }

        return true;
    }

    /// <summary>Answered with the safe default (the window is closing, a dialog was refused); nothing when already answered.</summary>
    public void Fallback() => Answer(_fallback);
}
