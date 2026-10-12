namespace NeonSidekick.Shell;

/// <summary>
/// A child's exit as <c>waitid</c> reports it, turned into the code <see cref="System.Diagnostics.Process.ExitCode"/> gives on Unix
/// (2026-10-12, <see cref="GroupChild"/>): <c>CLD_EXITED</c> is its status, a child a signal ended (<c>CLD_KILLED</c>,
/// <c>CLD_DUMPED</c>) is 128 plus the signal, as a shell reports it — 137 after SIGKILL — so a Mac's headers and alerts read as before.
/// Pure, tested on every system.
/// </summary>
public static class PosixExit
{
    public const int Exited = 1;
    public const int Killed = 2;
    public const int Dumped = 3;

    /// <summary>Whether <paramref name="code"/> (<c>si_code</c>) is an exit; macOS's <c>waitid</c> reports a stop as well, asked or not.</summary>
    public static bool IsExit(int code) => code is Exited or Killed or Dumped;

    /// <summary>The exit code for <paramref name="code"/> and <paramref name="status"/> (<c>si_code</c>, <c>si_status</c>); -1 for anything else.</summary>
    public static int Code(int code, int status) => code switch
    {
        Exited => status & 0xff,
        Killed or Dumped => 128 + status,
        _ => -1,
    };
}
