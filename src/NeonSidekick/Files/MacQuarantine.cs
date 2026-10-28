using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// A Mac's extended attributes for <see cref="MarkOfTheWeb"/> (2026-10-22, review O5): libc's <c>getxattr</c> and <c>setxattr</c> on the
/// file itself (<c>XATTR_NOFOLLOW</c>: a link's own attribute, never its target's), and <c>removexattr</c> since 2026-10-28. No new library: libSystem is every Mac process's.
/// Unproven until a Mac runs it; smoke <c>files:quarantine</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacQuarantine
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int NoFollow = 0x0001;

    /// <summary>The attribute's bytes, or null when the file has none.</summary>
    public static unsafe byte[]? Read(string path, string name)
    {
        nint size = GetXattr(path, name, null, 0, 0, NoFollow);
        if (size <= 0 || size > 64 * 1024)
        {
            return null;
        }

        byte[] value = new byte[size];
        fixed (byte* buffer = value)
        {
            nint read = GetXattr(path, name, buffer, (nuint)value.Length, 0, NoFollow);
            return read <= 0 ? null : value[..(int)read];
        }
    }

    /// <summary>Sets the attribute; false when the volume refused it.</summary>
    public static unsafe bool Write(string path, string name, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        fixed (byte* buffer = value)
        {
            return SetXattr(path, name, buffer, (nuint)value.Length, 0, NoFollow) == 0;
        }
    }

    /// <summary>
    /// Takes the attribute off (2026-10-28, the checkpoints plan: a restored file that had no mark loses the one a download left); true when
    /// it is gone, also when it was never there (<c>ENOATTR</c>).
    /// </summary>
    public static bool Remove(string path, string name) =>
        RemoveXattr(path, name, NoFollow) == 0 || Marshal.GetLastPInvokeError() == NoAttribute;

    private const int NoAttribute = 93;

    [LibraryImport(LibSystem, EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial nint GetXattr(string path, string name, byte* value, nuint size, uint position, int options);

    [LibraryImport(LibSystem, EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int SetXattr(string path, string name, byte* value, nuint size, uint position, int options);

    [LibraryImport(LibSystem, EntryPoint = "removexattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RemoveXattr(string path, string name, int options);
}
