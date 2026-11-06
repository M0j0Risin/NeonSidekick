using NeonSidekick.Files;

namespace NeonSidekick.Updates;

/// <summary>
/// A lock on one of <c>.update</c>'s two lock files (2026-11-04, self-update). .NET holds it as a share mode on Windows and an advisory <c>flock</c> on Unix, and the kernel releases it on a crash, so no process listing is needed and
/// nothing goes stale. Neither file is ever deleted, since a <c>flock</c> belongs to the file and a copy holding a deleted one would lock nothing another copy sees. A child process inherits no hold: .NET opens files non-inheritable
/// and close-on-exec on Unix, and the repo sets <c>FileShare.Inheritable</c> nowhere. Tested on Windows across two processes: shared allows shared and refuses exclusive, exclusive refuses both, and killing the holder frees it at once.
/// </summary>
public sealed class UpdateLock : IDisposable
{
    /// <summary>EWOULDBLOCK on a Mac: the one error .NET's <c>flock</c> call turns into an <see cref="IOException"/>, with the errno in its <c>HResult</c> (read from .NET 10's <c>SafeFileHandle.Unix.cs</c>).</summary>
    private const int MacWouldBlock = 35;

    /// <summary>The same on Linux, which this app doesn't ship for but the suite can run on.</summary>
    private const int LinuxWouldBlock = 11;

    private static readonly TimeSpan RetryStep = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _file;

    private UpdateLock(FileStream file)
    {
        _file = file;
    }

    /// <summary>A shared hold, or null when someone holds it exclusively or it can't be created: a read-only folder never stops the app, it just can't update, which preflight reports.</summary>
    public static UpdateLock? TryShared(string path) => TryOpen(path, FileAccess.Read, FileShare.Read, out _);

    /// <summary><see cref="TryShared(string)"/>, with <paramref name="held"/> true only when another holder is why it failed, so only that case waits (2026-11-04, review P5-2: an in-use lock that can't be opened read as an update in progress, 10 s at every start).</summary>
    public static UpdateLock? TryShared(string path, out bool held) => TryOpen(path, FileAccess.Read, FileShare.Read, out held);

    /// <summary>
    /// <see cref="TryExclusive(string, out bool)"/>, tried again while another holder is why it failed, up to <paramref name="limit"/>, the one such loop for the update's code (2026-11-04, reviews P11-12, P12-15: four had grown by hand, and <c>/update cancel</c>'s had none, so a start's tidy read as another copy downloading). <paramref name="stop"/> ends the wait early, with no hold.
    /// </summary>
    public static UpdateLock? TryExclusiveRetrying(string path, TimeSpan limit, Action<TimeSpan> pause, out bool held, Func<bool>? stop = null) => Retrying(path, FileAccess.ReadWrite, FileShare.None, limit, pause, out held, stop);

    /// <summary><see cref="TryShared(string, out bool)"/>, tried again the same way.</summary>
    public static UpdateLock? TrySharedRetrying(string path, TimeSpan limit, Action<TimeSpan> pause, out bool held) => Retrying(path, FileAccess.Read, FileShare.Read, limit, pause, out held, null);

    /// <summary>An exclusive hold, or null when anyone holds it.</summary>
    public static UpdateLock? TryExclusive(string path) => TryOpen(path, FileAccess.ReadWrite, FileShare.None, out _);

    /// <summary><see cref="TryExclusive(string)"/>, with <paramref name="held"/> as in <see cref="TryShared(string, out bool)"/>.</summary>
    public static UpdateLock? TryExclusive(string path, out bool held) => TryOpen(path, FileAccess.ReadWrite, FileShare.None, out held);

    public void Dispose() => _file.Dispose();

    /// <summary>
    /// Whether <paramref name="ex"/> says another holder has the lock: a sharing or lock violation on Windows, EWOULDBLOCK elsewhere. A bare <see cref="IOException"/> won't do, since write-protected media and disk errors throw it too.
    /// </summary>
    internal static bool IsHeld(IOException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return OperatingSystem.IsWindows() ? WriteResult.IsInUse(ex) : ex.HResult is MacWouldBlock or LinuxWouldBlock;
    }

    private static UpdateLock? Retrying(string path, FileAccess access, FileShare share, TimeSpan limit, Action<TimeSpan> pause, out bool held, Func<bool>? stop)
    {
        ArgumentNullException.ThrowIfNull(pause);
        var taken = TryOpen(path, access, share, out held);
        for (var waiting = TimeSpan.Zero; taken is null && held && waiting < limit; waiting += RetryStep)
        {
            pause(RetryStep);
            if (stop?.Invoke() == true)
            {
                return null;
            }

            taken = TryOpen(path, access, share, out held);
        }

        return taken;
    }

    private static UpdateLock? TryOpen(string path, FileAccess access, FileShare share, out bool held)
    {
        held = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new UpdateLock(new FileStream(path, FileMode.OpenOrCreate, access, share));
        }
        catch (IOException ex)
        {
            held = IsHeld(ex);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
