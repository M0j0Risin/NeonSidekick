using System.Diagnostics;
using System.Text;

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

    /// <summary>Whether it has exited but what it left can still be killed: a Mac's kept group leader (<see cref="GroupChild"/>), or a Windows job that still holds a child (<see cref="ProcessChild"/>).</summary>
    public virtual bool HoldsLeftovers => false;

    public abstract Task WaitForExitAsync();

    public abstract bool WaitForExit(int milliseconds);

    /// <summary>Ends the child and what it started: the job or the group, then the tree walk.</summary>
    public abstract void Kill();

    public abstract void Dispose();
}

/// <summary>
/// A <see cref="Process"/> and its job, 0 for none (off Windows, or when one could not be made). On Windows the child is started
/// suspended inside it (2026-10-19, the third shell review, <see cref="EmbeddedLlm.ChildJob.StartPiped"/>), its pipes the app's own;
/// where that cannot be done it was put in one right after the start (2026-10-12, the shell review, <see cref="EmbeddedLlm.ChildJob.CreateOwn"/>).
/// The kill ends the job whole before the tree walk, which stays for a child outside one; the dispose closes the job, which for a
/// background command's kill-on-close job ends what is left in it.
/// </summary>
internal sealed class ProcessChild : ShellChild
{
    private readonly Process _process;
    private readonly StreamWriter? _input;
    private readonly StreamReader? _output;
    private readonly StreamReader? _error;
    private nint _job;

    /// <summary>A child <c>Process.Start</c> started with its streams redirected, put in a job of its own now.</summary>
    public ProcessChild(Process process, bool killOnClose)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _job = EmbeddedLlm.ChildJob.CreateOwn(process, killOnClose);
    }

    /// <summary>A child started suspended in its job (<see cref="EmbeddedLlm.ChildJob.StartPiped"/>): the streams over its pipes, UTF-8 and buffered as <c>Process.Start</c> makes them.</summary>
    public ProcessChild(EmbeddedLlm.PipedChild piped)
    {
        ArgumentNullException.ThrowIfNull(piped);
        _process = piped.Process;
        _job = piped.Job;
        var utf8 = new UTF8Encoding(false);
        _input = new StreamWriter(new FileStream(piped.Input, FileAccess.Write, StreamBuffer, isAsync: false), utf8, StreamBuffer) { AutoFlush = true };
        _output = new StreamReader(new FileStream(piped.Output, FileAccess.Read, StreamBuffer, isAsync: false), utf8, detectEncodingFromByteOrderMarks: true, StreamBuffer);
        _error = new StreamReader(new FileStream(piped.Error, FileAccess.Read, StreamBuffer, isAsync: false), utf8, detectEncodingFromByteOrderMarks: true, StreamBuffer);
    }

    private const int StreamBuffer = 4096;

    public override int Pid => _process.Id;

    public override StreamWriter StandardInput => _input ?? _process.StandardInput;

    public override StreamReader StandardOutput => _output ?? _process.StandardOutput;

    public override StreamReader StandardError => _error ?? _process.StandardError;

    public override bool HasExited => _process.HasExited;

    public override int ExitCode => _process.ExitCode;

    public override bool InJob => _job != 0;

    // While a job still holds something after the shell itself exited, the kill must reach it (the code review, 2026-10-19;
    // 709bd39a fixed only the Mac's GroupChild): a `server & exit` whose server inherits stdout keeps the output pipe open, so the
    // pumps never drain and the session never completes, and ProcessSession.Kill skips a child whose HasExited is true unless it
    // HoldsLeftovers. The caller guards on the session not having exited, so this is asked only while something holds the command's
    // output: a program a command opened that let its output go (code .) completed the session and outlives it, as ChildJob.CreateOwn
    // promises. Only while a process is still in the job (the second shell review: a child gone on its own is not reported killed).
    // A foreground command's job too since the third review (the second had made it a background one's only, so a foreground
    // command's server holding its output outlived the timeout and Esc untracked, while a Mac's GroupChild killed it).
    public override bool HoldsLeftovers => EmbeddedLlm.ChildJob.HoldsProcesses(_job);

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
        try
        {
            _input?.Dispose();
        }
        catch (IOException)
        {
            // Its last flush into a pipe the child already closed.
        }

        _output?.Dispose();
        _error?.Dispose();
        _process.Dispose();
        EmbeddedLlm.ChildJob.Close(_job);
        _job = 0;
    }
}
