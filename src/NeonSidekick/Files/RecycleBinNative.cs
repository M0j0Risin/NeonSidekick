using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// Windows' Recycle bin (later on 2026-10-15, <c>File deletes to Recycle bin</c>): shell32's <c>SHFileOperationW</c> with <c>FO_DELETE</c> and
/// <c>FOF_ALLOWUNDO | FOF_NO_UI</c>, on a short STA thread under <see cref="RecycleBin.Cap"/> (<see cref="RecycleBin.RunCapped"/>). Before the
/// call: <see cref="RecycleBin.CheckWindows"/> (a full path, no <c>\\?\</c>, <c>MAX_PATH</c>; a UNC path has no bin) and the drive, which must
/// be fixed (a network or removable drive has no bin, and the shell would delete there for good without saying so). The answer is read
/// whole (<see cref="RecycleBin.MapWindows"/>: <c>fAnyOperationsAborted</c> too). Not <c>FOF_WANTNUKEWARNING</c>: its warning is a dialog,
/// and a delete from the model or the phone has nobody at the PC to answer; an item too big for the bin is deleted for good, as Windows
/// does, and the docs say so. <c>IFileOperation</c> is the newer API but promises nothing more here (the same nuke dialog), so one
/// blittable struct and one call it is. No new DLL (shell32 is the viewer's already); smoke <c>files:recycle-bin</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoUi = 0x0614;   // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR

    public RecycleResult Move(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (RecycleBin.CheckWindows(fullPath) is { } refused)
        {
            return refused;
        }

        if (!OnFixedDrive(fullPath))
        {
            return new RecycleResult(RecycleOutcome.NoBin);
        }

        return RecycleBin.RunCapped(() =>
        {
            var (code, aborted) = Call(fullPath);
            return RecycleBin.MapWindows(code, aborted);
        }, RecycleBin.Cap, sta: true);
    }

    /// <summary>The shell's call itself, its code and abort flag: the smoke's probe runs it on a path that does not exist.</summary>
    public static (int Code, bool Aborted) Call(string fullPath)
    {
        // pFrom is double-null-terminated: StringToHGlobalUni adds one, the path carries the other.
        nint from = Marshal.StringToHGlobalUni(fullPath + "\0");
        try
        {
            var op = new ShFileOpStruct { Func = FoDelete, From = from, Flags = (ushort)(FofAllowUndo | FofNoUi) };
            int code = SHFileOperationW(ref op);
            return (code, op.AnyOperationsAborted != 0);
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    private static bool OnFixedDrive(string fullPath)
    {
        try
        {
            return Path.GetPathRoot(fullPath) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public nint Window;
        public uint Func;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHFileOperationW(ref ShFileOpStruct op);
}
