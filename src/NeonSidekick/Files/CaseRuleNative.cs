using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// Windows' half of <see cref="CaseRule"/> (2026-10-22, review G16): a folder's case-sensitive flag (<c>fsutil file setCaseSensitiveInfo</c>,
/// WSL's folders) through <c>GetFileInformationByHandleEx</c>'s <c>FileCaseSensitiveInfo</c> on a handle opened for its attributes alone.
/// kernel32, which every process has; no new library. Null when the folder can't be opened or the volume has no such flag (FAT, a share
/// that answers no). Excluded from coverage with the other native layers; <see cref="CaseRuleTests"/> reads the flag on this PC's temp folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class CaseRuleNative
{
    private const uint ReadAttributes = 0x80;   // FILE_READ_ATTRIBUTES
    private const uint ShareAll = 0x7;   // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;   // FILE_FLAG_BACKUP_SEMANTICS: a folder's handle
    private const int CaseSensitiveInfo = 23;   // FileCaseSensitiveInfo
    private const uint CaseSensitiveDir = 0x1;   // FILE_CS_FLAG_CASE_SENSITIVE_DIR
    private static readonly nint Invalid = -1;

    /// <summary>Whether <paramref name="folder"/>'s names differ by case alone; null when it can't be asked.</summary>
    public static bool? IsCaseSensitive(string folder)
    {
        nint handle = CreateFile(folder, ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle == Invalid || handle == 0)
        {
            return null;
        }

        try
        {
            uint flags = 0;
            return GetFileInformationByHandleEx(handle, CaseSensitiveInfo, ref flags, sizeof(uint)) ? (flags & CaseSensitiveDir) != 0 : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(nint handle, int infoClass, ref uint info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
