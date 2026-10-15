using System.Security.Cryptography;
using System.Text;

namespace NeonSidekick.WebUi;

/// <summary>
/// A picture's file as the page knows it (later on 2026-10-15, the user's ask: the page follows the picture the PC points at): the first 16 hex
/// characters of SHA-256 over its full path (<see cref="Path.GetFullPath(string)"/>, upper-cased on Windows, whose file system ignores case), so
/// the page matches the strip's picture, the transcript's and the viewer's by file without ever seeing a path. Pure.
/// </summary>
public static class WebPictureKey
{
    public static string Of(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string path = Path.GetFullPath(fullPath);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(path), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }
}
