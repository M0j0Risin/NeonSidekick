using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Camera;

/// <summary>
/// The AVFoundation, CoreMedia, CoreVideo and libdispatch plumbing behind <see cref="MacCameraSystem"/> (2026-10-07, Stage 2: the
/// camera on macOS). The Objective-C side goes through <see cref="Viewer.AppKitNative"/>'s selectors and typed <c>objc_msgSend</c>
/// imports plus the shapes added here. Three pieces are made by hand:
/// <list type="bullet">
/// <item>The sample-buffer delegate is a class made at runtime (<c>NeonSidekickCameraDelegate</c>, <c>objc_allocateClassPair</c>) whose
/// <c>captureOutput:didOutputSampleBuffer:fromConnection:</c> is an <c>[UnmanagedCallersOnly]</c> function; each instance finds its
/// stream in <see cref="s_sinks"/>. It runs on the stream's own serial dispatch queue.</item>
/// <item>The permission's completion block is one <c>BLOCK_IS_GLOBAL</c> block (isa <c>_NSConcreteGlobalBlock</c>, never copied or
/// released by the runtime; <see cref="Screen.MacScreenNative"/>'s trick) made once and kept for the process: it carries no state, only
/// sets <see cref="s_answered"/>, so none of the per-call lifetimes ScreenCaptureKit's need arise.</item>
/// <item>AVFoundation logs a <c>WARNING: AVCaptureDeviceTypeExternal is deprecated for Continuity Cameras…</c> line to stderr on the
/// process's first discovery, whatever types are asked for (the exe has no Info.plist to say otherwise); the TUI owns the terminal, so
/// that first discovery runs with fd 2 pointed at /dev/null (<see cref="Quietly{T}"/>).</item>
/// </list>
/// An Objective-C exception is fatal here (NativeAOT cannot unwind one through managed frames; the spike's <c>setActiveVideoMinFrameDuration:</c>
/// with 1/30 on a 30.00003 fps range was one), so every call that may throw one is asked first (<c>canAddInput:</c>) or fed only values
/// AVFoundation gave (<see cref="MacCameraRules.DurationFor"/>). Excluded from coverage with <see cref="MacCameraSystem"/>; the smoke's
/// <c>camera:avf</c> and <c>--camera-check</c> prove it on the published exe.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacCameraNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    public const string AVFoundationPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    /// <summary><c>kCVPixelFormatType_32BGRA</c>: 'BGRA', blue first in memory, the BGRX the session wants (alpha opaque, unused).</summary>
    public const uint PixelFormatBgra = 0x42475241;

    /// <summary><c>kCVPixelBufferLock_ReadOnly</c>.</summary>
    public const ulong LockReadOnly = 1;

    private const int BlockIsGlobal = 1 << 28;
    private const int OpenWriteOnly = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Dimensions
    {
        public int Width;
        public int Height;
    }

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
        public BlockDescriptor OwnDescriptor;
    }

    /// <summary>A delegate instance's stream: told of each sample buffer on the delegate's queue.</summary>
    public interface ISink
    {
        void Arrived(nint sampleBuffer);
    }

    private static readonly Lock s_gate = new();
    private static readonly ConcurrentDictionary<nint, ISink> s_sinks = new();
    private static readonly ManualResetEventSlim s_answered = new();
    private static nint s_delegateClass;
    private static BlockLiteral* s_accessBlock;
    private static bool s_quieted;

    // ---- Objective-C shapes AppKitNative has no import for ----

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendNintNintLong(nint receiver, nint selector, nint a, nint b, long c);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendNintPtr(nint receiver, nint selector, nint a, nint* error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBoolPtr(nint receiver, nint selector, nint* error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBoolNint(nint receiver, nint selector, nint a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendUInt32Number(nint receiver, nint selector, uint value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial CMTime SendTime(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidTime(nint receiver, nint selector, CMTime time);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidNintBlock(nint receiver, nint selector, nint a, nint block);

    [LibraryImport(ObjC)]
    public static partial nint objc_lookUpClass(byte* name);

    // ---- CoreMedia ----

    [LibraryImport(CoreMedia)]
    public static partial nint CMSampleBufferGetImageBuffer(nint sampleBuffer);

    [LibraryImport(CoreMedia)]
    public static partial Dimensions CMVideoFormatDescriptionGetDimensions(nint description);

    [LibraryImport(CoreMedia)]
    public static partial uint CMFormatDescriptionGetMediaSubType(nint description);

    // ---- CoreVideo ----

    [LibraryImport(CoreVideo)]
    public static partial nint CVPixelBufferRetain(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial void CVPixelBufferRelease(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferLockBaseAddress(nint buffer, ulong flags);

    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferUnlockBaseAddress(nint buffer, ulong flags);

    [LibraryImport(CoreVideo)]
    public static partial void* CVPixelBufferGetBaseAddress(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetBytesPerRow(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetWidth(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial nuint CVPixelBufferGetHeight(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial uint CVPixelBufferGetPixelFormatType(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial byte CVPixelBufferIsPlanar(nint buffer);

    [LibraryImport(CoreVideo)]
    public static partial int CVPixelBufferCreate(nint allocator, nuint width, nuint height, uint format, nint attributes, nint* buffer);

    // ---- libdispatch and libc ----

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint dispatch_queue_create(string label, nint attributes);

    [LibraryImport(LibSystem)]
    public static partial void dispatch_sync_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(LibSystem)]
    public static partial void dispatch_release(nint queue);

    [LibraryImport(LibSystem, EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport(LibSystem, EntryPoint = "dup2")]
    private static partial int Dup2(int fd, int fd2);

    [LibraryImport(LibSystem, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int CloseFile(int fd);

    // ---- AVFoundation's constants and classes ----

    /// <summary>AVFoundation loaded; false where the framework is not there.</summary>
    public static bool Load() => NativeLibrary.TryLoad(AVFoundationPath, out _);

    /// <summary>An exported <c>NSString *</c> constant (<c>AVMediaTypeVideo</c>…): the export is the variable's address. 0 when missing.</summary>
    public static nint Constant(string library, string name) =>
        NativeLibrary.TryLoad(library, out nint handle) && NativeLibrary.TryGetExport(handle, name, out nint export) ? *(nint*)export : 0;

    public static nint MediaTypeVideo => Constant(AVFoundationPath, "AVMediaTypeVideo");

    /// <summary>A class by name, or 0 when it is not there (never throws, unlike <see cref="Viewer.AppKitNative.Class"/>).</summary>
    public static nint TryClass(string name)
    {
        _ = Load();
        int length = System.Text.Encoding.UTF8.GetByteCount(name);
        byte* bytes = stackalloc byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(name, new Span<byte>(bytes, length));
        bytes[length] = 0;
        return objc_lookUpClass(bytes);
    }

    /// <summary><c>[AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo]</c>, or -1 when AVFoundation cannot be reached.</summary>
    public static long Authorization()
    {
        nint device = TryClass("AVCaptureDevice");
        nint video = MediaTypeVideo;
        return device == 0 || video == 0 ? -1 : (long)Send(device, Sel("authorizationStatusForMediaType:"), video);
    }

    /// <summary>
    /// <c>requestAccessForMediaType:completionHandler:</c> with the process's one global block, waiting up to <paramref name="wait"/>
    /// for the user's answer; true when an answer came (read the status again for what it was). macOS shows its question for the
    /// terminal, the app responsible for this process.
    /// </summary>
    public static bool RequestAccess(TimeSpan wait)
    {
        nint device = TryClass("AVCaptureDevice");
        nint video = MediaTypeVideo;
        if (device == 0 || video == 0)
        {
            return false;
        }

        s_answered.Reset();
        SendVoidNintBlock(device, Sel("requestAccessForMediaType:completionHandler:"), video, (nint)AccessBlock());
        return s_answered.Wait(wait);
    }

    /// <summary>The permission's global block, made once (the smoke makes it too, never calling it).</summary>
    public static nint AccessBlock()
    {
        lock (s_gate)
        {
            if (s_accessBlock is null)
            {
                var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));   // kept for the process
                block->Isa = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock");
                block->Flags = BlockIsGlobal;
                block->Invoke = (nint)(delegate* unmanaged<BlockLiteral*, byte, void>)&Answered;
                block->OwnDescriptor = new BlockDescriptor { Reserved = 0, Size = (nuint)sizeof(BlockLiteral) };
                block->Descriptor = &block->OwnDescriptor;
                s_accessBlock = block;
            }

            return (nint)s_accessBlock;
        }
    }

    [UnmanagedCallersOnly]
    private static void Answered(BlockLiteral* block, byte granted) => s_answered.Set();

    /// <summary>
    /// <paramref name="work"/> with stderr pointed at /dev/null the first time it runs in the process (AVFoundation's deprecation line
    /// comes on the first discovery only); later calls run plainly. Nothing else of ours writes to stderr while the TUI runs.
    /// </summary>
    public static T Quietly<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (s_gate)
        {
            if (s_quieted)
            {
                return work();
            }

            s_quieted = true;
            int saved = Dup(2);
            int sink = OpenFile("/dev/null", OpenWriteOnly);
            bool redirected = saved >= 0 && sink >= 0 && Dup2(sink, 2) >= 0;
            try
            {
                return work();
            }
            finally
            {
                if (redirected)
                {
                    _ = Dup2(saved, 2);
                }

                if (saved >= 0)
                {
                    _ = CloseFile(saved);
                }

                if (sink >= 0)
                {
                    _ = CloseFile(sink);
                }
            }
        }
    }

    // ---- The sample-buffer delegate ----

    /// <summary>The delegate class (made once), conforming to <c>AVCaptureVideoDataOutputSampleBufferDelegate</c>.</summary>
    public static nint DelegateClass
    {
        get
        {
            lock (s_gate)
            {
                if (s_delegateClass == 0)
                {
                    nint cls = objc_allocateClassPair(objc_getClass("NSObject"), "NeonSidekickCameraDelegate", 0);
                    class_addMethod(cls, Sel("captureOutput:didOutputSampleBuffer:fromConnection:"),
                        (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidOutput, "v@:@^{opaqueCMSampleBuffer=}@");
                    if (objc_getProtocol("AVCaptureVideoDataOutputSampleBufferDelegate") is var protocol and not 0)
                    {
                        class_addProtocol(cls, protocol);
                    }

                    objc_registerClassPair(cls);
                    s_delegateClass = cls;
                }

                return s_delegateClass;
            }
        }
    }

    /// <summary>A new delegate instance (owned by the caller) that hands its buffers to <paramref name="sink"/>.</summary>
    public static nint NewDelegate(ISink sink)
    {
        nint instance = Send(Send(DelegateClass, Sel("alloc")), Sel("init"));
        s_sinks[instance] = sink;
        return instance;
    }

    /// <summary>The instance's sink forgotten: after the queue is fenced (<see cref="Fence"/>), so no callback still holds it.</summary>
    public static void Forget(nint instance) => s_sinks.TryRemove(instance, out _);

    /// <summary>Waits until every block already on <paramref name="queue"/> has run (a callback in flight finishes).</summary>
    public static void Fence(nint queue) => dispatch_sync_f(queue, 0, &Nothing);

    [UnmanagedCallersOnly]
    private static void Nothing(nint context)
    {
    }

    [UnmanagedCallersOnly]
    private static void DidOutput(nint self, nint selector, nint output, nint sampleBuffer, nint connection)
    {
        try
        {
            if (s_sinks.TryGetValue(self, out var sink))
            {
                sink.Arrived(sampleBuffer);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing may leave an unmanaged callback: this frame is lost, the reader waits for the next.
            Diagnostics.DiagnosticLog.Warn("Camera", "A camera frame could not be taken: " + ex.Message);
        }
    }

    // ---- NSError ----

    /// <summary>An NSError's words and code (0 and null for nil).</summary>
    public static (string? Message, long Code) Error(nint error) =>
        error == 0 ? (null, 0) : (FromNSString(Send(error, Sel("localizedDescription"))), SendLong(error, Sel("code")));
}
