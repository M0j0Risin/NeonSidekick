using System.Runtime.InteropServices;

namespace NeonSidekick.Files;

/// <summary>
/// The user's Downloads folder (2026-11-13, <c>/settings export</c>, the user's call: the zip lands there, outside the model's working
/// folder). On Windows the known folder (<c>FOLDERID_Downloads</c>), which the user may have moved; on a Mac <c>Downloads</c> under the
/// user profile (never <c>$HOME</c> read here: only <c>EnvironmentOverrides</c> reads variables). <see cref="Pick"/> holds the choice and is
/// covered on every OS; <see cref="KnownFolder"/> is the thin shell32 call, freed with <c>CoTaskMemFree</c>, as
/// <see cref="RecycleBinOperation"/> imports its own.
/// </summary>
internal static unsafe partial class DownloadsFolder
{
    /// <summary><c>FOLDERID_Downloads</c>.</summary>
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The folder to use: <paramref name="knownFolder"/> when it is set, else <c>&lt;userProfile&gt;/Downloads</c>. Pure.</summary>
    public static string Pick(string? knownFolder, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(userProfile);
        return string.IsNullOrWhiteSpace(knownFolder) ? Path.Combine(userProfile, "Downloads") : knownFolder;
    }

    /// <summary>This user's Downloads folder (not created here).</summary>
    public static string Resolve() =>
        Pick(OperatingSystem.IsWindows() ? KnownFolder() : null, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The Downloads known folder's path on Windows; null elsewhere or when the shell answers a failure.</summary>
    internal static string? KnownFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        Guid id = DownloadsId;
        nint path = 0;
        try
        {
            int hr = SHGetKnownFolderPath(&id, 0, 0, out path);
            return hr < 0 || path == 0 ? null : Marshal.PtrToStringUni(path);
        }
        finally
        {
            if (path != 0)
            {
                CoTaskMemFree(path);
            }
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(Guid* id, uint flags, nint token, out nint path);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint memory);
}
