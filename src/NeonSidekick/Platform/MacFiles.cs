using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NeonSidekick.Platform;

/// <summary>
/// A file's own type on a Mac, and a read that opens only a regular file (2026-10-20, the code review of the crash reports: a FIFO named
/// <c>x.ips</c> blocked <see cref="FileStream"/>'s open forever, a pool thread lost per call, and a link named <c>x.diag</c> handed any
/// readable file to the model). .NET's <see cref="FileSystemInfo"/> tells a link but not a FIFO, a socket or a device from a regular file,
/// so this asks <c>lstat</c>, and <see cref="OpenRegular"/> opens with <c>O_NOFOLLOW | O_NONBLOCK</c> (a link is refused by the open
/// itself, a FIFO opens at once) and checks <c>fstat</c>'s type on the very descriptor it reads, so a file swapped between the check and
/// the open is still caught. macOS's <c>struct stat</c> keeps <c>st_mode</c> at offset 4 in the 64-bit inode layout: arm64's only one, and
/// x64's under the <c>$INODE64</c> names, which the x64 calls use (the plain x64 names keep the old layout, <c>st_mode</c> at 8).
/// Excluded from coverage with the other native layers; proven by the Mac's <c>MacFileLogTests</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe partial class MacFiles
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int ReadOnly = 0;   // O_RDONLY
    private const int NonBlocking = 0x4;   // O_NONBLOCK
    private const int NoFollow = 0x100;   // O_NOFOLLOW
    private const int CloseOnExec = 0x0100_0000;   // O_CLOEXEC
    private const int TypeMask = 0xF000;   // S_IFMT
    private const int RegularType = 0x8000;   // S_IFREG
    private const int DirectoryType = 0x4000;   // S_IFDIR
    private const int LinkType = 0xA000;   // S_IFLNK
    private const int NoEntry = 2;   // ENOENT
    private const int Denied = 13;   // EACCES
    private const int NotPermitted = 1;   // EPERM
    private const int TooManyLinks = 62;   // ELOOP: O_NOFOLLOW met a link

    /// <summary>What a path is, its last part never followed.</summary>
    public enum Kind
    {
        Missing,
        Regular,
        Directory,
        Link,
        Other,
    }

    /// <summary>What <paramref name="path"/> is (<c>lstat</c>: a link is a link, never what it points at).</summary>
    public static Kind KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte* buffer = stackalloc byte[256];
        int status = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStat64(path, buffer) : LStat(path, buffer);
        return status != 0 ? Kind.Missing : KindOfMode(*(ushort*)(buffer + 4));
    }

    /// <summary>
    /// <paramref name="path"/> opened for reading when it is a regular file; null when it is a link, a FIFO, a socket, a device or a folder.
    /// Throws as <see cref="FileStream"/> would for a missing file (<see cref="FileNotFoundException"/>) or a refused one
    /// (<see cref="UnauthorizedAccessException"/>).
    /// </summary>
    public static FileStream? OpenRegular(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int fd = Open(path, ReadOnly | NonBlocking | NoFollow | CloseOnExec);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            return errno switch
            {
                TooManyLinks => null,
                NoEntry => throw new FileNotFoundException(null, path),
                Denied or NotPermitted => throw new UnauthorizedAccessException(path),
                _ => throw new IOException(Marshal.GetPInvokeErrorMessage(errno), errno),
            };
        }

        byte* buffer = stackalloc byte[256];
        int status = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? FStat64(fd, buffer) : FStat(fd, buffer);
        if (status != 0 || KindOfMode(*(ushort*)(buffer + 4)) != Kind.Regular)
        {
            _ = Close(fd);
            return null;
        }

        return new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, 4096);
    }

    /// <summary>The file's hard-link count (<c>lstat</c>'s <c>st_nlink</c>, at offset 6 in the 64-bit inode layout; 2026-10-22, <see cref="Files.FileLinks"/>); 1 when it can't be read.</summary>
    public static int LinkCount(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte* buffer = stackalloc byte[256];
        int status = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LStat64(path, buffer) : LStat(path, buffer);
        return status != 0 ? 1 : Math.Max(1, (int)*(ushort*)(buffer + 6));
    }

    private static Kind KindOfMode(ushort mode) => (mode & TypeMask) switch
    {
        RegularType => Kind.Regular,
        DirectoryType => Kind.Directory,
        LinkType => Kind.Link,
        _ => Kind.Other,
    };

    [LibraryImport(LibSystem, EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "fstat")]
    private static partial int FStat(int fd, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat64(string path, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "fstat$INODE64")]
    private static partial int FStat64(int fd, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int Close(int fd);
}
