using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>A file's or folder's identity on its volume: the volume and the file's number there (Windows' serial and file index, a Mac's <c>st_dev</c> and <c>st_ino</c>).</summary>
public readonly record struct FileId(ulong Volume, ulong File);

/// <summary>
/// Which file a path reaches, whatever its spelling (2026-10-23, the admin review's M2): the home was matched by its spelling, so a
/// <c>\\localhost\C$\…</c>, <c>\\PC\Users\…</c>, a <c>subst</c> or a mapped drive spelling of <c>configs\tasks.json</c> passed
/// <see cref="WriteGuard.Judge"/>. Windows opens the path for its attributes only (backup semantics, so a folder opens too) and reads
/// <c>GetFileInformationByHandle</c>'s volume serial and file index (kernel32, as <see cref="FileLinks"/> does; the same pair through
/// <c>\\localhost\C$</c> and the local path, checked on this PC 2026-10-23); a Mac reads <c>stat</c>'s <c>st_dev</c> and <c>st_ino</c>
/// (links followed: the folder a path goes through). Null when the path can't be opened or on another system.
/// </summary>
public static class FileIdentity
{
    /// <summary>The identity of the file or folder at <paramref name="path"/>, or null.</summary>
    public static FileId? Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return FileIdentityNative.Windows(path);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Platform.MacFiles.Identity(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    /// <summary>
    /// The ancestor of <paramref name="full"/> (itself included when it exists) whose identity is <paramref name="target"/>, spelled as
    /// <paramref name="full"/> spells it; null when none is. Walks up from the deepest part that exists, at most 64 parts.
    /// </summary>
    public static string? AncestorWith(string full, FileId target)
    {
        ArgumentNullException.ThrowIfNull(full);
        string? current = Path.TrimEndingDirectorySeparator(full);
        for (int depth = 0; !string.IsNullOrEmpty(current) && depth < 64; depth++)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && Of(current) is { } id && id == target)
            {
                return current;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length >= current.Length)
            {
                break;
            }

            current = parent;
        }

        return null;
    }
}

/// <summary>Windows' half of <see cref="FileIdentity"/>. Excluded from coverage with the native layers.</summary>
[SupportedOSPlatform("windows")]
internal static partial class FileIdentityNative
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;

    public static unsafe FileId? Windows(string path)
    {
        nint handle = CreateFile(path, FileReadAttributes, ShareAll, 0, OpenExisting, FlagBackupSemantics, 0);
        if (handle == -1 || handle == 0)
        {
            return null;
        }

        try
        {
            byte* info = stackalloc byte[52];   // BY_HANDLE_FILE_INFORMATION: the serial at 28, the index's high and low at 44 and 48
            if (!GetFileInformationByHandle(handle, info))
            {
                return null;
            }

            ulong index = ((ulong)*(uint*)(info + 44) << 32) | *(uint*)(info + 48);
            return new FileId(*(uint*)(info + 28), index);
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
    private static unsafe partial bool GetFileInformationByHandle(nint handle, byte* info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
