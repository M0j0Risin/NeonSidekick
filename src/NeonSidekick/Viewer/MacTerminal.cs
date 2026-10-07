using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The terminal on a Mac (2026-10-07, Stage 2 phase 4): this process's ancestors read once at startup through libproc's
/// <c>proc_pidinfo</c> (<c>PROC_PIDT_SHORTBSDINFO</c>, the short form any user may read — the full one is refused for root's
/// <c>login</c>, which stands between the shell and Terminal), and on TAB the app <see cref="TerminalPick"/> chooses brought forward
/// with <c>NSRunningApplication activateWithOptions:</c> — allowed, since the app asking is the active one. Startup touches no AppKit;
/// the pick runs on the main thread. Excluded from coverage with the AppKit layer.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacTerminal
{
    private const int ShortBsdInfo = 13;      // PROC_PIDT_SHORTBSDINFO
    private const int ShortBsdInfoSize = 64;  // sizeof(struct proc_bsdshortinfo)
    private const int ParentOffset = 4;       // pbsi_ppid
    private const long RegularPolicy = 0;     // NSApplicationActivationPolicyRegular: an app with a Dock icon

    private const int PathMax = 4096;         // PROC_PIDPATHINFO_MAXSIZE

    private static IReadOnlyList<int> s_ancestors = [];
    private static IReadOnlyList<string?> s_ancestorPaths = [];
    private static string? s_termProgram;
    private static nint s_terminal;           // the NSRunningApplication chosen, retained (main thread)

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int size);

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int proc_pidpath(int pid, void* buffer, uint size);

    /// <summary>The ancestors and the terminal's name kept (startup, any thread; no AppKit).</summary>
    public static void Remember(string? termProgram)
    {
        s_termProgram = termProgram;
        s_ancestors = TerminalPick.Ancestors(Environment.ProcessId, ParentOf);
        s_ancestorPaths = s_ancestors.Select(PathOf).ToList();   // for /terminal (2026-10-07): no AppKit, any thread
        DiagnosticLog.Debug("Viewer", $"Ancestors: {string.Join(" → ", s_ancestors)}; TERM_PROGRAM {termProgram ?? "(none)"}.");
    }

    /// <summary>
    /// <c>/usr/bin/open</c>'s arguments for a new terminal in <paramref name="folder"/> (2026-10-07, <c>/terminal</c>): the pure
    /// <see cref="TerminalPick.OpenTerminalArguments"/> over the ancestors' executables read at startup. Any thread, no AppKit.
    /// </summary>
    public static IReadOnlyList<string> OpenArguments(string folder) => TerminalPick.OpenTerminalArguments(s_ancestorPaths, s_termProgram, folder);

    /// <summary>The terminal brought forward (main thread); false when none was found.</summary>
    public static bool Focus()
    {
        nint terminal = Terminal();
        if (terminal == 0)
        {
            DiagnosticLog.Debug("Viewer", "No terminal found: TAB does nothing.");
            return false;
        }

        if (SendBoolULong(terminal, Sel("activateWithOptions:"), 0) == 0)
        {
            DiagnosticLog.Debug("Viewer", "The terminal could not be brought forward.");
            return false;
        }

        return true;
    }

    /// <summary>What was chosen, for the smoke: the app's name and pid and how it was found, or none (main thread).</summary>
    public static string Describe()
    {
        nint terminal = Terminal();
        return terminal == 0
            ? $"no terminal app among {s_ancestors.Count} ancestors"
            : $"the terminal is {FromNSString(Send(terminal, Sel("localizedName"))) ?? "?"} (pid {SendInt(terminal, Sel("processIdentifier"))})";
    }

    // The app chosen once and kept while it runs: the first ancestor with a Dock icon, else TERM_PROGRAM's app, else the app that
    // was in front before one of ours took the keyboard (AppKitWindow's).
    private static nint Terminal()
    {
        if (s_terminal != 0 && SendBool(s_terminal, Sel("isTerminated")) == 0)
        {
            return s_terminal;
        }

        nint found = 0;
        if (TerminalPick.FirstApp(s_ancestors, pid => AppOf(pid) != 0) is int app)
        {
            found = AppOf(app);
        }
        else if (TerminalPick.BundleFor(s_termProgram) is { } bundle)
        {
            nint apps = Send(Class("NSRunningApplication"), Sel("runningApplicationsWithBundleIdentifier:"), NSString(bundle));
            found = SendULong(apps, Sel("count")) > 0 ? SendIndex(apps, Sel("objectAtIndex:"), 0) : 0;
        }

        found = found != 0 ? found : AppKitWindow.BeforeApp;
        if (s_terminal != 0)
        {
            SendVoid(s_terminal, Sel("release"));
        }

        s_terminal = found == 0 ? 0 : Send(found, Sel("retain"));
        return s_terminal;
    }

    // An app with a Dock icon running as pid, or 0.
    private static nint AppOf(int pid)
    {
        nint app = SendIndex(Class("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), (nuint)pid);
        return app != 0 && SendLong(app, Sel("activationPolicy")) == RegularPolicy ? app : 0;
    }

    // A process's executable, or null when it cannot be read (root's login is readable: proc_pidpath needs no privilege).
    private static string? PathOf(int pid)
    {
        byte* path = stackalloc byte[PathMax];
        int length = proc_pidpath(pid, path, PathMax);
        return length > 0 ? Marshal.PtrToStringUTF8((nint)path, length) : null;
    }

    private static int? ParentOf(int pid)
    {
        byte* info = stackalloc byte[ShortBsdInfoSize];
        return proc_pidinfo(pid, ShortBsdInfo, 0, info, ShortBsdInfoSize) == ShortBsdInfoSize ? *(int*)(info + ParentOffset) : null;
    }
}
