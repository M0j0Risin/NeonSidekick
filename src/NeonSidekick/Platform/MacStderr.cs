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
/// <para>
/// Three guards (2026-10-20, the code review). A scope ends by itself at its ceiling (<see cref="Ceiling"/>, past
/// <c>MacLogAccess.Timeout</c>): a read the caller gave up on may sit in one native call that never returns, and fd 2 must not stay on
/// /dev/null for the rest of the run because of it; what that call prints after the ceiling reaches the terminal, the lesser harm. Every
/// scope's start, the last one's end and a restore bump a generation, and the linger's restore runs only while its own generation is still
/// the latest, checked under the lock: <see cref="Timer.Dispose()"/> cannot recall a callback already dispatched, so without it a stale
/// restore could put fd 2 back under a later scope (the 9a0a736f leak's shape). And the saved copy of fd 2 and the /dev/null descriptor are
/// close-on-exec (<c>F_DUPFD_CLOEXEC</c>, <c>O_CLOEXEC</c>), so a child started meanwhile never inherits the terminal's copy as a stray
/// descriptor; it does inherit fd 2 as /dev/null while a scope is open, which is what the redirect is.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacStderr
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int WriteOnly = 1;   // O_WRONLY
    private const int OpenCloseOnExec = 0x0100_0000;   // O_CLOEXEC on macOS
    private const int DupCloseOnExecCommand = 67;   // F_DUPFD_CLOEXEC on macOS
    private const int GetFlagsCommand = 1;   // F_GETFD
    private const int CloseOnExecFlag = 1;   // FD_CLOEXEC

    /// <summary>How long fd 2 stays quiet after the last scope ends.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(1);

    /// <summary>The longest one scope keeps fd 2 quiet before it ends by itself (2026-10-20): past the event logs' 30 s timeout.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(45);

    private static readonly object s_gate = new();
    private static int s_depth;
    private static int s_saved = -1;
    private static Timer? s_restore;
    private static long s_generation;

    /// <summary>
    /// A quiet scope: dispose it to end it (the last one out restores stderr). It ends by itself after <paramref name="ceiling"/>
    /// (<see cref="Ceiling"/> when null), whatever its owner is doing.
    /// </summary>
    public static IDisposable Quiet(TimeSpan? ceiling = null)
    {
        lock (s_gate)
        {
            s_generation++;   // a restore already dispatched is stale from here
            s_restore?.Dispose();
            s_restore = null;
            if (s_depth++ == 0 && s_saved < 0)
            {
                int saved = DupCloseOnExec(2);
                int sink = OpenFile("/dev/null", WriteOnly | OpenCloseOnExec);
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

        return new Scope(ceiling ?? Ceiling);
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
                long generation = ++s_generation;
                s_restore = new Timer(_ => RestoreIf(generation), null, Linger, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>The linger's end: fd 2 back only while no scope has started or ended since it was set (2026-10-20, the code review).</summary>
    private static void RestoreIf(long generation)
    {
        lock (s_gate)
        {
            if (generation == s_generation)
            {
                Restore();
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

            s_generation++;
            s_restore?.Dispose();
            s_restore = null;
            _ = Dup2(s_saved, 2);
            _ = CloseFile(s_saved);
            s_saved = -1;
        }
    }

    /// <summary>Whether the saved copy of fd 2 is close-on-exec (2026-10-20, the code review's check); null while none is saved.</summary>
    public static bool? SavedCloseOnExec
    {
        get
        {
            lock (s_gate)
            {
                return s_saved < 0 ? null : (FcntlGet(s_saved, GetFlagsCommand) & CloseOnExecFlag) != 0;
            }
        }
    }

    /// <summary>
    /// <c>fcntl(fd, F_DUPFD_CLOEXEC, 0)</c> (2026-10-20, the code review): a copy of <paramref name="fd"/> no child inherits, made in one step.
    /// <c>fcntl</c> is variadic, and Apple's arm64 ABI passes a variadic argument on the stack, never in a register, so the arm64 call spells
    /// x2 to x7 as padding to put the lowest descriptor wanted (0) in the first stack slot, where <c>va_arg</c> reads it; x64 passes it in a
    /// register as a plain argument. A refusal falls back to <c>dup</c>, the old behavior.
    /// </summary>
    private static int DupCloseOnExec(int fd)
    {
        int copy = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? FcntlStacked(fd, DupCloseOnExecCommand, 0, 0, 0, 0, 0, 0, 0)
            : FcntlArgument(fd, DupCloseOnExecCommand, 0);
        return copy >= 0 ? copy : Dup(fd);
    }

    /// <summary>One quiet scope: ended by its owner's dispose or by its ceiling, whichever comes first, once.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Timer _ceiling;
        private int _left;

        public Scope(TimeSpan ceiling)
        {
            _ceiling = new Timer(static state => ((Scope)state!).End(byCeiling: true), this, ceiling, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => End(byCeiling: false);

        private void End(bool byCeiling)
        {
            if (Interlocked.Exchange(ref _left, 1) != 0)
            {
                return;
            }

            _ceiling.Dispose();
            if (byCeiling)
            {
                Diagnostics.DiagnosticLog.Debug("Mac", "a quiet stderr scope reached its ceiling and was ended (a native call that has not returned)");
            }

            Leave();
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

    [LibraryImport(LibSystem, EntryPoint = "fcntl")]
    private static partial int FcntlStacked(int fd, int command, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint argument);

    [LibraryImport(LibSystem, EntryPoint = "fcntl")]
    private static partial int FcntlArgument(int fd, int command, nint argument);

    [LibraryImport(LibSystem, EntryPoint = "fcntl")]
    private static partial int FcntlGet(int fd, int command);

    [LibraryImport(LibSystem, EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport(LibSystem, EntryPoint = "dup2")]
    private static partial int Dup2(int fd, int fd2);

    [LibraryImport(LibSystem, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int CloseFile(int fd);
}
