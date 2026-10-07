using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Perf;

/// <summary>
/// The Metal device's memory facts (2026-10-07, the embedded LLM on a Mac, the user's pick over RAM × ⅔): Metal.framework's
/// <c>MTLCreateSystemDefaultDevice</c> and three Objective-C messages through libobjc's <c>objc_msgSend</c> — every argument
/// and result a pointer or a 64-bit integer, which arm64 passes in registers as a plain C call does, so nothing is marshalled
/// by value. Both are system libraries (nothing ships beside the exe); the smoke's <c>llm:metal</c> reads them on the
/// published binary.
///
/// <para><c>recommendedMaxWorkingSetSize</c> is the unified memory Metal lets the GPU use — the number llama.cpp prints
/// for <c>MTL0</c> and sizes its fit to (an M4 with 16 GiB: 10922 MiB, two thirds; raised by <c>iogpu.wired_limit_mb</c>,
/// which this reads too since Metal does). So it stands where Windows reads a card's dedicated memory
/// (<see cref="GpuMemory.DedicatedBytes"/>).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MetalNative
{
    private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Metal)]
    private static partial nint MTLCreateSystemDefaultDevice();

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial ulong SendUInt64(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPointer(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);

    /// <summary>The default Metal device's name and recommended working set in bytes; null when there is no device (a VM without one).</summary>
    public static (string Name, long WorkingSetBytes)? Read()
    {
        nint device = MTLCreateSystemDefaultDevice();   // a +1 reference: released below
        if (device == 0)
        {
            return null;
        }

        try
        {
            long workingSet = (long)SendUInt64(device, sel_registerName("recommendedMaxWorkingSetSize"));
            nint name = SendPointer(device, sel_registerName("name"));
            string text = name == 0 ? "" : Marshal.PtrToStringUTF8(SendPointer(name, sel_registerName("UTF8String"))) ?? "";
            return (text, workingSet);
        }
        finally
        {
            SendVoid(device, sel_registerName("release"));
        }
    }
}
