using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// The watcher that stops a Mac's background commands when the app dies however it dies (2026-10-12, the crash watcher, the user's
/// ask; <see cref="EmbeddedLlm.LlamaGuard"/>'s shape). Windows' kill-on-close job ends a background command's process tree with the
/// app, a crash too; macOS has no such thing, and the app's own kill (the registry's dispose, the exit signals' <see cref="ShellGroups.KillTracked"/>)
/// never runs after a crash, a force quit or a <c>kill -9</c>. So the app starts its own executable once, at its first background
/// command, as <c>NeonSidekick --shell-guard &lt;records&gt; &lt;owner pid&gt;</c> with stdin a pipe it holds and never writes; the
/// kernel closes that pipe when the app dies, and at its end of file the guard kills every group the records show its owner started
/// (<see cref="ShellRecords.KillOwned"/>) and exits. A normal exit has killed and unrecorded them already: the guard finds none.
///
/// <para>The guard sits in the app's process group, so the terminal's hangup reaches it too; it ignores SIGHUP, SIGINT, SIGTERM and SIGQUIT
/// and goes only at its end of file, which the app's own end (by those signals or any other way) brings. It starts nothing: the counted
/// process-start site is <see cref="ShellGroups"/>' start of it. What it cannot cover — killed together with the app (<c>pkill -9
/// NeonSidekick</c>) — the next start's <see cref="ShellRecords.Sweep"/> does. Mac only.</para>
/// </summary>
public static class ShellGuard
{
    /// <summary>The argument that makes the executable a guard. Pinned.</summary>
    public const string Flag = "--shell-guard";

    /// <summary>Whether <paramref name="args"/> ask for the guard: the flag, the records folder and the owner pid.</summary>
    public static bool Asked(string[] args) =>
        args is { Length: 3 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>The guard's command line after its executable: the flag, the records folder, the owner (this process).</summary>
    public static IReadOnlyList<string> Arguments(string recordsFolder) =>
        [Flag, recordsFolder, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)];

    /// <summary>The guard's run (<paramref name="args"/> as <see cref="Asked"/> takes them): 0 once its owner's groups are killed, 2 for a bad owner pid.</summary>
    [SupportedOSPlatform("macos")]
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string records = args[1];
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int owner))
        {
            await Console.Error.WriteLineAsync("shell-guard: the owner pid is not a number").ConfigureAwait(false);
            return 2;
        }

        // Only the end of the pipe ends the guard: the terminal's hangup and a logout's TERM reach it with the app, whose end follows.
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Ignore);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Ignore);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Ignore);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Ignore);
        using (var stdin = Console.OpenStandardInput())
        {
            await Task.Run(() =>
            {
                var buffer = new byte[256];
                while (stdin.Read(buffer) > 0)
                {
                    // Nothing is sent: only the end of the pipe means anything.
                }
            }).ConfigureAwait(false);
        }

        // Quietly: whoever read the guard's output went with the app.
        ShellRecords.KillOwned(records, owner, KillGroup);
        return 0;
    }

    private static void Ignore(PosixSignalContext context) => context.Cancel = true;

    /// <summary><c>killpg(pgid, SIGKILL)</c>; ESRCH (every member gone) is no error.</summary>
    [SupportedOSPlatform("macos")]
    internal static void KillGroup(int pgid)
    {
        if (PosixSpawnNative.KillPg(pgid, PosixSpawnNative.SigKill) != 0)
        {
            DiagnosticLog.Debug(ShellKinds.Category, $"group {pgid.ToString(CultureInfo.InvariantCulture)}: killpg errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Whether group <paramref name="pgid"/> has a member this user may signal (<c>killpg(pgid, 0)</c>).</summary>
    [SupportedOSPlatform("macos")]
    internal static bool GroupAlive(int pgid) => PosixSpawnNative.KillPg(pgid, 0) == 0;
}
