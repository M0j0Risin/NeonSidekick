namespace NeonSidekick.Updates;

/// <summary>
/// A lock on one of <c>.update</c>'s two lock files (2026-11-04, self-update). .NET holds it as a share mode on Windows and an advisory <c>flock</c> on Unix, and the kernel releases it on a crash, so no process listing is needed and
/// nothing goes stale. Neither file is ever deleted, since a <c>flock</c> belongs to the file and a copy holding a deleted one would lock nothing another copy sees. A child process inherits no hold: .NET opens files non-inheritable
/// and close-on-exec on Unix, and the repo sets <c>FileShare.Inheritable</c> nowhere. Tested on Windows across two processes: shared allows shared and refuses exclusive, exclusive refuses both, and killing the holder frees it at once.
/// </summary>
public sealed class UpdateLock : IDisposable
{
    private readonly FileStream _file;

    private UpdateLock(FileStream file)
    {
        _file = file;
    }

    /// <summary>A shared hold, or null when someone holds it exclusively or it can't be created: a read-only folder never stops the app, it just can't update, which preflight reports.</summary>
    public static UpdateLock? TryShared(string path) => TryOpen(path, FileAccess.Read, FileShare.Read);

    /// <summary>An exclusive hold, or null when anyone holds it.</summary>
    public static UpdateLock? TryExclusive(string path) => TryOpen(path, FileAccess.ReadWrite, FileShare.None);

    public void Dispose() => _file.Dispose();

    private static UpdateLock? TryOpen(string path, FileAccess access, FileShare share)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new UpdateLock(new FileStream(path, FileMode.OpenOrCreate, access, share));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
