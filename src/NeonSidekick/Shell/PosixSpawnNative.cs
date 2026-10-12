using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Shell;

/// <summary>
/// libc's process doors for <see cref="GroupChild"/> (2026-10-12, the Mac's orphans): <c>posix_spawnp</c> with its attribute and
/// file-action objects (opaque pointers on macOS, made by their <c>_init</c>), <c>waitid</c>/<c>waitpid</c>, <c>killpg</c>, and
/// <c>getsid</c>/<c>getpgid</c> for the smoke's <c>shell:posix-spawn</c>. The flag values, <c>sigset_t</c> (a 32-bit mask) and
/// <c>siginfo_t</c>'s first fields are <b>macOS's</b>, read from the SDK's headers and spiked in C on 15.7. The chdir action is the
/// <c>_np</c> one: the plain <c>posix_spawn_file_actions_addchdir</c> is macOS 26's and not exported on 15.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class PosixSpawnNative
{
    private const string LibC = "libc";

    /// <summary>A session of its own: the child leads it and its process group (pgid = sid = pid), with no controlling terminal.</summary>
    public const short SetSid = 0x0400;

    /// <summary>The signals in the default set go back to their default action (an ignored one would survive exec: .NET ignores SIGPIPE).</summary>
    public const short SetSigDef = 0x0004;

    /// <summary>The child's signal mask is the one given (empty).</summary>
    public const short SetSigMask = 0x0008;

    /// <summary>Apple's: every descriptor not named by a file action is closed in the child.</summary>
    public const short CloexecDefault = 0x4000;

    public const int SigKill = 9;
    public const int SigStop = 17;

    /// <summary><c>idtype_t</c>'s <c>P_PID</c>.</summary>
    public const int PPid = 1;

    public const int WExited = 0x04;
    public const int WNoWait = 0x20;

    public const int Eintr = 4;
    public const int Echild = 10;

    /// <summary><c>siginfo_t</c> is 104 bytes on macOS; room to spare.</summary>
    public const int SigInfoBytes = 128;

    /// <summary><c>si_code</c> and <c>si_status</c>'s offsets in <c>siginfo_t</c> (after <c>si_signo</c>, <c>si_errno</c>; then <c>si_pid</c>, <c>si_uid</c>).</summary>
    public const int SiCodeOffset = 8;
    public const int SiStatusOffset = 20;

    /// <summary><c>posix_spawnp</c>: a name with a slash is used as it is, a bare one looked up on the PATH, as <c>Process.Start</c> does.</summary>
    [LibraryImport(LibC, EntryPoint = "posix_spawnp")]
    public static partial int Spawn(int* pid, byte* file, nint* fileActions, nint* attributes, byte** argv, byte** envp);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_init")]
    public static partial int FileActionsInit(nint* fileActions);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_destroy")]
    public static partial int FileActionsDestroy(nint* fileActions);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_adddup2")]
    public static partial int FileActionsAddDup2(nint* fileActions, int fd, int newFd);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    public static partial int FileActionsAddChdir(nint* fileActions, byte* path);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_init")]
    public static partial int AttrInit(nint* attributes);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_destroy")]
    public static partial int AttrDestroy(nint* attributes);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setflags")]
    public static partial int AttrSetFlags(nint* attributes, short flags);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setsigdefault")]
    public static partial int AttrSetSigDefault(nint* attributes, uint* signals);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setsigmask")]
    public static partial int AttrSetSigMask(nint* attributes, uint* signals);

    [LibraryImport(LibC, EntryPoint = "waitid", SetLastError = true)]
    public static partial int WaitId(int idType, uint id, byte* info, int options);

    [LibraryImport(LibC, EntryPoint = "waitpid", SetLastError = true)]
    public static partial int WaitPid(int pid, int* status, int options);

    [LibraryImport(LibC, EntryPoint = "killpg", SetLastError = true)]
    public static partial int KillPg(int pgid, int signal);

    [LibraryImport(LibC, EntryPoint = "getsid", SetLastError = true)]
    public static partial int GetSid(int pid);

    [LibraryImport(LibC, EntryPoint = "getpgid", SetLastError = true)]
    public static partial int GetPgid(int pid);
}
