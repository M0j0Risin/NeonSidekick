using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// How many names a file has (2026-10-22, review I4, the fix plan's mechanism 7): a write by temp file and rename gives the target a new file,
/// so its other hard links kept the old text, silently. <see cref="WorkingDirectory.WriteAtomically"/> writes a file with more than one link in
/// place instead, keeping every name on the one file. Windows asks <c>GetFileInformationByHandle</c> (kernel32), a Mac <c>lstat</c>'s
/// <c>st_nlink</c> (<see cref="Platform.MacFiles.LinkCount"/>). 1 when the count can't be had.
/// </summary>
public static class FileLinks
{
    /// <summary>The number of hard links to the file at <paramref name="full"/>; 1 when unknown.</summary>
    public static int Count(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return FileLinksNative.Count(full);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Platform.MacFiles.LinkCount(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return 1;
    }
}

/// <summary>Windows' half of <see cref="FileLinks"/>: <c>nNumberOfLinks</c> from <c>GetFileInformationByHandle</c>. Excluded from coverage with the native layers.</summary>
[SupportedOSPlatform("windows")]
internal static partial class FileLinksNative
{
    public static unsafe int Count(string full)
    {
        using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte* info = stackalloc byte[52];   // BY_HANDLE_FILE_INFORMATION
        return GetFileInformationByHandle(handle.DangerousGetHandle(), info) ? Math.Max(1, *(int*)(info + 40)) : 1;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(nint handle, byte* info);
}
