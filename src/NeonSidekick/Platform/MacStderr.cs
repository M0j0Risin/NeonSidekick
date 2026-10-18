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
/// keeps its file.
/// </summary>
[SupportedOSPlatform("macos")]
public static partial class MacStderr
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int WriteOnly = 1;   // O_WRONLY

    private static readonly object s_gate = new();
    private static int s_depth;
    private static int s_saved = -1;

    /// <summary>A quiet scope: dispose it to end it (the last one out restores stderr).</summary>
    public static IDisposable Quiet()
    {
        lock (s_gate)
        {
            if (s_depth++ == 0)
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
                _ = Dup2(s_saved, 2);
                _ = CloseFile(s_saved);
                s_saved = -1;
            }
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

    [LibraryImport(LibSystem, EntryPoint = "dup")]
    private static partial int Dup(int fd);

    [LibraryImport(LibSystem, EntryPoint = "dup2")]
    private static partial int Dup2(int fd, int fd2);

    [LibraryImport(LibSystem, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int CloseFile(int fd);
}
