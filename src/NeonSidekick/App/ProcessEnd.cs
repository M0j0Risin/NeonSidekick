using System.Runtime.Versioning;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.App;

/// <summary>
/// How the app ends on Windows (2026-10-24, the user's report: now and then a NeonSidekick.exe stayed alive after its window was
/// gone, holding the Web UI's port). A <c>return</c> from <c>Main</c> and the console's default close handler both end in
/// <c>ExitProcess</c>, which kills every other thread and then runs each loaded DLL's <c>DLL_PROCESS_DETACH</c>: a thread killed
/// while it held a lock a detach waits on (NVML's, sampled once a second by the performance bar, was the suspect; a spike that day
/// hung 0 of 300 times, so it is unproven) leaves the process stuck for good with one thread. <c>TerminateProcess</c> on the
/// process itself runs no detach. Called last, once the run's own work is flushed and disposed; the kernel still closes every
/// handle, so the kill-on-close jobs take their children as before. Not a process-start site and no new native dependency
/// (<see cref="JobObjectNative.TerminateProcess"/>, already declared). It would end the test host, so it has no test.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessEnd
{
    /// <summary>The current-process pseudo-handle, <c>GetCurrentProcess()</c>'s value.</summary>
    private const nint CurrentProcess = -1;

    /// <summary>
    /// <c>STATUS_CONTROL_C_EXIT</c>, the code the console's default handler ends a process with: the window close's, so the
    /// process's exit record reads as it did before the app ended it itself.
    /// </summary>
    public const uint ControlCExit = 0xC000013A;

    /// <summary>Ends this process at once with <paramref name="exitCode"/>; returns only if the call failed.</summary>
    public static void Now(uint exitCode) => _ = JobObjectNative.TerminateProcess(CurrentProcess, exitCode);
}
