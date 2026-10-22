using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// Windows' Recycle bin (later on 2026-10-15, <c>File deletes to Recycle bin</c>), on a short STA thread under <see cref="RecycleBin.Cap"/>
/// (<see cref="RecycleBin.RunCapped"/>). Before the call: <see cref="RecycleBin.CheckWindows"/> (a full path, no <c>\\?\</c>, <c>MAX_PATH</c>; a UNC
/// path has no bin) and the drive, which must be fixed (a network or removable drive has no bin). The call is <c>IFileOperation</c> with a
/// progress sink since 2026-10-22 (review J1, <see cref="RecycleBinOperation"/>): <c>SHFileOperationW</c> deleted an item too big for the bin
/// for good and answered 0, read as "recycled"; the sink aborts such an item, which is not deleted and is said (<see cref="FileText.RecycleWouldDeleteForGood"/>).
/// The answer is read whole (<see cref="RecycleBin.MapWindows"/>: an abort too). No new DLL; smoke <c>files:recycle-bin</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsRecycleBin : IRecycleBin
{
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
            var (code, aborted, notRecyclable) = Call(fullPath);
            return RecycleBin.MapWindows(code, aborted, notRecyclable);
        }, RecycleBin.Cap, sta: true);
    }

    /// <summary>
    /// <see cref="Move"/>'s own checks before the shell, on the purge's folder (2026-10-20, the code review): a UNC path or a drive that is not
    /// fixed has no bin; a folder whose path is already too long for the shell can never send it an entry. Anything that passes here is the
    /// shell's to answer per entry. The same checks <see cref="Move"/> makes, so a purge that passed this never meets "no bin" part-way.
    /// </summary>
    public RecycleResult? Probe(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        return RecycleBin.CheckWindows(fullPath) switch
        {
            { Outcome: RecycleOutcome.NoBin } noBin => noBin,
            { } refused => refused.Message == FileText.RecycleTooLong ? new RecycleResult(RecycleOutcome.Failed, FileText.RecycleFolderTooLong) : refused,
            null => OnFixedDrive(fullPath) ? null : new RecycleResult(RecycleOutcome.NoBin),
        };
    }

    /// <summary>The shell's call itself: its HRESULT, abort flag and whether the bin could not take the item; the smoke's probe runs it on a path that does not exist.</summary>
    public static (int Code, bool Aborted, bool NotRecyclable) Call(string fullPath) => RecycleBinOperation.Recycle(fullPath);

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
}
