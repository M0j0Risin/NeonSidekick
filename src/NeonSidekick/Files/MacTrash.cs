using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Files;

/// <summary>
/// A Mac's Trash (later on 2026-10-15, <c>File deletes to Recycle bin</c>, "Trash" on a Mac): <c>[[NSFileManager defaultManager]
/// trashItemAtURL:resultingItemURL:error:]</c> inside an autorelease pool of the call's own, off the caller's thread under
/// <see cref="RecycleBin.Cap"/>. No volume is checked first (Apple's advice: call it and handle the failure); a volume with no Trash
/// answers <c>NSFeatureUnsupportedError</c>, read as <see cref="RecycleOutcome.NoBin"/> (<see cref="RecycleBin.MapMac"/>), so the caller
/// deletes directly and says so, as Finder does; any other error (513, no permission) refuses. Called bare, never inside an
/// <c>NSFileCoordinator</c> block of our own: one around it can hang in a folder a sync app watches. Unproven until a Mac runs it;
/// smoke <c>files:trash</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class MacTrash : IRecycleBin
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>The selector the Trash is moved by (macOS 10.8+).</summary>
    public const string TrashSelector = "trashItemAtURL:resultingItemURL:error:";

    public RecycleResult Move(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return RecycleBin.RunCapped(() => Trash(fullPath), RecycleBin.Cap, sta: false);
    }

    /// <summary>
    /// Always null, go on and try (2026-10-20, the code review asked what the Trash could tell before a purge's question): nothing cheap and
    /// sure. A file system's type is no answer (a FAT or exFAT stick has a <c>.Trashes</c> of its own, a network volume may not), and
    /// <c>URLForDirectory:NSTrashDirectory … appropriateForURL:</c> answers nil with <c>create:NO</c> for a volume whose Trash isn't made
    /// yet and makes one with <c>create:YES</c>. So the purge asks for the Trash and, when the first move answers no Trash, stops with
    /// nothing deleted for good and asks again in a for-good question (<see cref="PurgeStop.NoBin"/>).
    /// </summary>
    public RecycleResult? Probe(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return null;
    }

    private static RecycleResult Trash(string fullPath)
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
            nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(fullPath));
            nint error = 0;
            if (SendTrash(manager, Sel(TrashSelector), url, 0, &error) != 0)
            {
                return new RecycleResult(RecycleOutcome.Recycled);
            }

            return error == 0
                ? new RecycleResult(RecycleOutcome.Failed, FileText.RecycleRefused(0))
                : RecycleBin.MapMac(SendLong(error, Sel("code")), FromNSString(Send(error, Sel("localizedDescription"))));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The smoke's probe: whether NSFileManager answers <see cref="TrashSelector"/>. Trashes nothing.</summary>
    public static bool Answers()
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
            return manager != 0 && SendBoolNint(manager, Sel("respondsToSelector:"), Sel(TrashSelector)) != 0;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, nint resulting, nint* error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolNint(nint receiver, nint selector, nint a);
}
