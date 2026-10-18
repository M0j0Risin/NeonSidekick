using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Platform;

/// <summary>
/// Stderr pointed at /dev/null for a while on a Mac (2026-10-18, the Mac event-log plan): the TUI owns the terminal, and Apple's
/// frameworks write there unasked — OSLog's "Wall Clock adjustment detected - results might be strange while using --end" on every
/// unified-log read (Phase 0), AVFoundation's deprecation line on the camera's first discovery (<c>MacCameraNative.Quietly</c>, which
/// uses this scope since). One process-wide, reference-counted scope under one lock, so overlapping quiet calls on two threads never
/// leave fd 2 silenced: the first in saves fd 2 and points it at /dev/null, the last out puts it back. While a scope is open nothing
/// of ours reaches stderr (the app's own diagnostics go through <c>DiagnosticLog</c>); a crash print in that moment is lost, as the
/// crash handler's report is not. Excluded from coverage with the other native layers; the smoke's <c>eventlog:oslog</c> checks fd 2
/// keeps its file. The last one out restores fd 2 only after <see cref="Linger"/> (later on 2026-10-18, the phase 5 check: OSLog writes
/// its line from a background queue a few milliseconds after an enumerator is made, so two of nine probe reads printed it just after
/// their scope had closed); a scope opened inside the linger keeps the redirect and cancels the restore.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacStderr
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int WriteOnly = 1;   // O_WRONLY

    /// <summary>How long fd 2 stays quiet after the last scope ends.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(1);

    private static readonly object s_gate = new();
    private static int s_depth;
    private static int s_saved = -1;
    private static Timer? s_restore;

    /// <summary>A quiet scope: dispose it to end it (the last one out restores stderr).</summary>
    public static IDisposable Quiet()
    {
        lock (s_gate)
        {
            s_restore?.Dispose();
            s_restore = null;
            if (s_depth++ == 0 && s_saved < 0)
            {
                int saved = Dup(2);
                int sink = OpenFile("/dev/null", WriteOnly);
                if (saved >= 0 && sink >= 0 && Dup2(sink, 2) >= 0)
                {
                    s_saved = saved;
                }
                else if (saved >= 0)
                {
                    _ = CloseFile(saved);
                }

                if (sink >= 0)
                {
                    _ = CloseFile(sink);
                }
            }
        }

        return new Scope();
    }

    /// <summary>How many quiet scopes are open (for the smoke's check that all were closed).</summary>
    public static int Depth
    {
        get
        {
            lock (s_gate)
            {
                return s_depth;
            }
        }
    }

    private static void Leave()
    {
        lock (s_gate)
        {
            if (s_depth == 0 || --s_depth > 0)
            {
                return;
            }

            if (s_saved >= 0)
            {
                s_restore?.Dispose();
                s_restore = new Timer(_ => Restore(), null, Linger, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Whether fd 2 is pointed away now (a scope open, or the linger after one).</summary>
    public static bool Silenced
    {
        get
        {
            lock (s_gate)
            {
                return s_saved >= 0;
            }
        }
    }

    /// <summary>Puts fd 2 back now when no scope is open (the linger's end; the smoke and the tests, which compare it at once).</summary>
    public static void Restore()
    {
        lock (s_gate)
        {
            if (s_depth > 0 || s_saved < 0)
            {
                return;
            }

            s_restore?.Dispose();
            s_restore = null;
            _ = Dup2(s_saved, 2);
            _ = CloseFile(s_saved);
            s_saved = -1;
        }
    }

    private sealed class Scope : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                Leave();
            }
        }
    }

    /// <summary>
    /// The inode fd 2 points at now (2026-10-18, the smoke's <c>eventlog:oslog</c>): the same before and after a read proves the quiet scope
    /// put the terminal back. arm64's <c>struct stat</c> has the 64-bit inode at offset 8; null when <c>fstat</c> fails.
    /// </summary>
    public static unsafe ulong? Inode(int fd = 2)
    {
        byte* buffer = stackalloc byte[256];
        return FStat(fd, buffer) == 0 ? *(ulong*)(buffer + 8) : null;
    }

    [LibraryImport(LibSystem, EntryPoint = "fstat")]
    private static unsafe partial int FStat(int fd, byte* buffer);

    [LibraryImport(LibSystem, EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport(LibSystem, EntryPoint = "dup2")]
    private static partial int Dup2(int fd, int fd2);

    [LibraryImport(LibSystem, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int CloseFile(int fd);
}
