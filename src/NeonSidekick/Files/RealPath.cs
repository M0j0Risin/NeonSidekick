using System.Runtime.InteropServices;

namespace NeonSidekick.Files;

/// <summary>
/// A path with every link along it resolved, the way libgit2 hands paths back (2026-10-06, the first Mac smoke): on macOS
/// <c>/var</c>, <c>/tmp</c> and <c>/etc</c> are links into <c>/private</c>, so the temp folder's <c>/var/folders/…</c> comes back from
/// libgit2 as <c>/private/var/folders/…</c>, which no root spelled the user's way contains. libc's <c>realpath</c>; null on Windows
/// (libgit2 keeps the spelling it was given there) and when the path cannot be resolved.
/// </summary>
internal static partial class RealPath
{
    private const string LibC = "libc";

    public static string? Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        nint resolved = NativeRealPath(path, 0);
        if (resolved == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [LibraryImport(LibC, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NativeRealPath(string path, nint resolved);

    [LibraryImport(LibC, EntryPoint = "free")]
    private static partial void Free(nint pointer);
}
