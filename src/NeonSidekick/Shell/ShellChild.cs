using System.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// What <see cref="ProcessSession"/> and the runner's pumps need of a started child (2026-10-12, the Mac's orphans): its three
/// streams, its exit, its kill and its handle's end. Two kinds: <see cref="ProcessChild"/>, a <see cref="Process"/> and its job
/// (Windows, as it was), and <see cref="GroupChild"/>, a macOS child spawned as the leader of a session of its own so a kill can end
/// its whole process group. The members are <see cref="Process"/>'s, with its exceptions, so the session's catches hold for both.
/// </summary>
internal abstract class ShellChild : IDisposable
{
    public abstract int Pid { get; }

    public abstract StreamWriter StandardInput { get; }

    public abstract StreamReader StandardOutput { get; }

    public abstract StreamReader StandardError { get; }

    /// <summary>Whether the child itself has exited (what it started may live on).</summary>
    public abstract bool HasExited { get; }

    /// <summary>The child's exit code; <see cref="InvalidOperationException"/> while it runs.</summary>
    public abstract int ExitCode { get; }

    /// <summary>Whether a job of its own holds it (Windows).</summary>
    public virtual bool InJob => false;

    /// <summary>Whether it has exited but what it left can still be killed (a Mac's kept group leader, <see cref="GroupChild"/>).</summary>
    public virtual bool HoldsLeftovers => false;

    public abstract Task WaitForExitAsync();

    public abstract bool WaitForExit(int milliseconds);

    /// <summary>Ends the child and what it started: the job or the group, then the tree walk.</summary>
    public abstract void Kill();

    public abstract void Dispose();
}

/// <summary>
/// A <see cref="Process"/> and the job it was put in right after the start (2026-10-12, the shell review,
/// <see cref="EmbeddedLlm.ChildJob.CreateOwn"/>, 0 for none: off Windows, or when one could not be made). The kill ends the job whole
/// before the tree walk, which stays for a child outside one; the dispose closes the job, which for a background command's
/// kill-on-close job ends what is left in it.
/// </summary>
internal sealed class ProcessChild : ShellChild
{
    private readonly Process _process;
    private nint _job;

    public ProcessChild(Process process, bool killOnClose)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _job = EmbeddedLlm.ChildJob.CreateOwn(process, killOnClose);
    }

    public override int Pid => _process.Id;

    public override StreamWriter StandardInput => _process.StandardInput;

    public override StreamReader StandardOutput => _process.StandardOutput;

    public override StreamReader StandardError => _process.StandardError;

    public override bool HasExited => _process.HasExited;

    public override int ExitCode => _process.ExitCode;

    public override bool InJob => _job != 0;

    public override Task WaitForExitAsync() => _process.WaitForExitAsync(CancellationToken.None);

    public override bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public override void Kill()
    {
        // The job first (2026-10-12): it holds what the tree walk misses, a grandchild whose parent already exited. The walk
        // stays for a child that runs outside one; on one already gone it does nothing.
        EmbeddedLlm.ChildJob.Terminate(_job, -1);
        _process.Kill(entireProcessTree: true);
    }

    public override void Dispose()
    {
        _process.Dispose();
        EmbeddedLlm.ChildJob.Close(_job);
        _job = 0;
    }
}
