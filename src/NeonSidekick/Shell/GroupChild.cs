using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Shell;

/// <summary>
/// A shell child on a Mac, started as the leader of a session of its own (2026-10-12, the Mac's orphans; the user's calls). On
/// Windows each command runs in a job (<see cref="ProcessChild"/>); off Windows that is a no-op, and <c>Process.Kill(entireProcessTree)</c>
/// is a walk of parent ids, which misses a grandchild launchd adopted once its parent exited (a helper that started a server and quit).
/// <c>Process.Start</c> cannot make a child a group leader, so this is the shell runner's start site spelled with libc's
/// <c>posix_spawnp</c> (<see cref="PosixSpawnNative"/>) — the same counted site, a second way through it, a design decision the user
/// made on 2026-10-12:
/// <list type="bullet">
/// <item><b>The session.</b> <c>POSIX_SPAWN_SETSID</c> rather than <c>SETPGROUP</c> (the user's pick): the child leads a new session
/// and its process group (pgid = pid), with no controlling terminal, so a <c>/dev/tty</c> open (sudo's password, ssh's host key) fails
/// at once with ENXIO rather than stopping the command on SIGTTIN until its timeout — or, as before, reading the TUI's keys. Everything
/// the command starts stays in the group unless it starts a session of its own (zsh <c>-c</c> runs without job control).</item>
/// <item><b>The kill.</b> The tree walk first, as before (it stops each process before reading its children, and finds a descendant
/// that left the group while its parent lives), then <c>killpg(pid, SIGKILL)</c>, which ends every member, the adopted orphans too.</item>
/// <item><b>The pid kept while it matters.</b> A waiter thread learns the exit with <c>waitid(WNOWAIT)</c>, which leaves the leader a
/// zombie. While the zombie lives neither its pid nor its group id can be used again, so a <c>killpg</c> never reaches a stranger's group.
/// It is reaped at once unless a background command left members running (<c>killpg(pid, 0)</c> answers 0 for a live member, EPERM for
/// the zombie alone, measured on 15.7); then only in <see cref="Dispose"/>, after the group kill, so <c>ps</c> shows a
/// <c>(zsh) &lt;defunct&gt;</c> just while there is something left to kill. (.NET's own SIGCHLD handling reaps only the children .NET started — unless the
/// app began with SIGCHLD ignored; a child reaped elsewhere answers ECHILD, its code is -1, and no group kill is sent after that.)</item>
/// <item><b>A background command's leftovers</b> (<c>tiedToApp</c>, the user's call): the dispose kills the group even after the leader
/// exited, as Windows' kill-on-close job does at its close — the registry's close, its eviction and the app's exit all come here. A
/// foreground command's leftovers are left alone, as there. A crash still stops nothing on a Mac.</item>
/// <item><b>The rest as <c>Process.Start</c> does it:</b> three CLOEXEC pipes (.NET's own anonymous pipes) put on 0/1/2, every other
/// descriptor closed in the child (<c>POSIX_SPAWN_CLOEXEC_DEFAULT</c>); every signal back to its default and none blocked (.NET ignores
/// SIGPIPE, and an ignored signal survives exec); the environment from <see cref="ChildEnvironment.Apply"/> over a
/// <see cref="ProcessStartInfo"/> made for it, so the withheld secrets stay one code path; the exit code as .NET reports it on Unix
/// (<see cref="PosixExit"/>: 137 after SIGKILL); UTF-8 streams, stdin flushed on every write.</item>
/// </list>
/// Spiked in C on macOS 15.7 the same day (session and group ids, <c>waitid</c>'s codes, the zombie kept, ENXIO on <c>/dev/tty</c>).
/// The smoke's <c>shell:posix-spawn</c> proves it on the published binary.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class GroupChild : ShellChild
{
    private readonly int _pid;
    private readonly bool _tiedToApp;
    private readonly AnonymousPipeServerStream _stdin;
    private readonly AnonymousPipeServerStream _stdout;
    private readonly AnonymousPipeServerStream _stderr;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private int _exitCode;
    private bool _exitSeen;
    private bool _reapedElsewhere;
    private bool _disposed;
    private bool _reaped;

    private GroupChild(int pid, bool tiedToApp, AnonymousPipeServerStream stdin, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr)
    {
        _pid = pid;
        _tiedToApp = tiedToApp;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        var utf8 = new UTF8Encoding(false);
        StandardInput = new StreamWriter(stdin, utf8) { AutoFlush = true };
        StandardOutput = new StreamReader(stdout, utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        StandardError = new StreamReader(stderr, utf8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        var waiter = new Thread(Wait) { IsBackground = true, Name = "shell-wait " + pid.ToString(CultureInfo.InvariantCulture) };
        waiter.Start();
    }

    /// <summary>Spawns <paramref name="launch"/> as a session leader; <paramref name="tiedToApp"/> for a background command (see the class).</summary>
    /// <exception cref="ShellStartException">It could not be started: the sentence names the program and libc's reason.</exception>
    public static GroupChild Start(ProcessLaunch launch, bool tiedToApp)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string name = Path.GetFileName(launch.Executable);
        if (launch.ArgumentList is not { } arguments)
        {
            // The raw string is cmd.exe's (ShellCommandLine), and there is no cmd here.
            throw new ShellStartException(ShellText.CouldNotStart(name, ShellText.RawArgumentsAreCmds));
        }

        var environment = new ProcessStartInfo();
        ChildEnvironment.Apply(environment, launch);
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        int pid;
        int error;
        try
        {
            error = Spawn(
                launch.Executable,
                [launch.Executable, .. arguments],
                environment.Environment.Where(e => e.Value is not null).Select(e => e.Key + "=" + e.Value).ToList(),
                launch.WorkingDirectory,
                (int)stdin.ClientSafePipeHandle.DangerousGetHandle(),
                (int)stdout.ClientSafePipeHandle.DangerousGetHandle(),
                (int)stderr.ClientSafePipeHandle.DangerousGetHandle(),
                out pid);
        }
        finally
        {
            // The child has its copies on 0/1/2; ours would keep the pipes open past its exit.
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
        }

        if (error != 0)
        {
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw new ShellStartException(ShellText.CouldNotStart(name, Marshal.GetPInvokeErrorMessage(error)));
        }

        return new GroupChild(pid, tiedToApp, stdin, stdout, stderr);
    }

    /// <summary>One <c>posix_spawnp</c> of <paramref name="file"/> as a session leader, its three streams on 0/1/2 and nothing else open; 0 or the errno.</summary>
    private static int Spawn(string file, IReadOnlyList<string> argv, IReadOnlyList<string> envp, string workingDirectory, int stdin, int stdout, int stderr, out int pid)
    {
        var strings = new List<nint>();
        nint actions = 0;
        nint attributes = 0;
        bool actionsMade = false;
        bool attributesMade = false;
        try
        {
            byte* path = Utf8(file, strings);
            byte** args = Vector(argv, strings);
            byte** env = Vector(envp, strings);
            byte* folder = Utf8(workingDirectory, strings);
            int error = PosixSpawnNative.FileActionsInit(&actions);
            if (error != 0)
            {
                pid = 0;
                return error;
            }

            actionsMade = true;
            error = PosixSpawnNative.AttrInit(&attributes);
            if (error != 0)
            {
                pid = 0;
                return error;
            }

            attributesMade = true;
            // Every catchable signal back to its default (SIGKILL and SIGSTOP cannot be set), none blocked.
            uint defaults = ~((1u << (PosixSpawnNative.SigKill - 1)) | (1u << (PosixSpawnNative.SigStop - 1)));
            uint mask = 0;
            short flags = PosixSpawnNative.SetSid | PosixSpawnNative.SetSigDef | PosixSpawnNative.SetSigMask | PosixSpawnNative.CloexecDefault;
            error = PosixSpawnNative.FileActionsAddDup2(&actions, stdin, 0);
            error = error != 0 ? error : PosixSpawnNative.FileActionsAddDup2(&actions, stdout, 1);
            error = error != 0 ? error : PosixSpawnNative.FileActionsAddDup2(&actions, stderr, 2);
            error = error != 0 ? error : PosixSpawnNative.FileActionsAddChdir(&actions, folder);
            error = error != 0 ? error : PosixSpawnNative.AttrSetFlags(&attributes, flags);
            error = error != 0 ? error : PosixSpawnNative.AttrSetSigDefault(&attributes, &defaults);
            error = error != 0 ? error : PosixSpawnNative.AttrSetSigMask(&attributes, &mask);
            int spawned = 0;
            error = error != 0 ? error : PosixSpawnNative.Spawn(&spawned, path, &actions, &attributes, args, env);
            pid = error == 0 ? spawned : 0;
            return error;
        }
        finally
        {
            if (actionsMade)
            {
                PosixSpawnNative.FileActionsDestroy(&actions);
            }

            if (attributesMade)
            {
                PosixSpawnNative.AttrDestroy(&attributes);
            }

            foreach (nint s in strings)
            {
                NativeMemory.Free((void*)s);
            }
        }
    }

    private static byte* Utf8(string text, List<nint> owned)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        var native = (byte*)NativeMemory.Alloc((nuint)length + 1);
        owned.Add((nint)native);
        Encoding.UTF8.GetBytes(text, new Span<byte>(native, length));
        native[length] = 0;
        return native;
    }

    private static byte** Vector(IReadOnlyList<string> items, List<nint> owned)
    {
        var vector = (byte**)NativeMemory.Alloc((nuint)(items.Count + 1), (nuint)sizeof(byte*));
        owned.Add((nint)vector);
        for (int i = 0; i < items.Count; i++)
        {
            vector[i] = Utf8(items[i], owned);
        }

        vector[items.Count] = null;
        return vector;
    }

    public override int Pid => _pid;

    public override StreamWriter StandardInput { get; }

    public override StreamReader StandardOutput { get; }

    public override StreamReader StandardError { get; }

    public override bool HasExited => _exited.Task.IsCompleted;

    public override int ExitCode => HasExited ? _exitCode : throw new InvalidOperationException("the process is still running");

    public override Task WaitForExitAsync() => _exited.Task;

    public override bool WaitForExit(int milliseconds) => _exited.Task.Wait(milliseconds);

    /// <summary>The waiter: the exit learnt without reaping, then reaped here unless a background command's group still has members (see the class).</summary>
    private void Wait()
    {
        var info = stackalloc byte[PosixSpawnNative.SigInfoBytes];
        int code;
        int pause = 10;
        while (true)
        {
            new Span<byte>(info, PosixSpawnNative.SigInfoBytes).Clear();
            if (PosixSpawnNative.WaitId(PosixSpawnNative.PPid, (uint)_pid, info, PosixSpawnNative.WExited | PosixSpawnNative.WNoWait) == 0)
            {
                int siCode = *(int*)(info + PosixSpawnNative.SiCodeOffset);
                if (PosixExit.IsExit(siCode))
                {
                    code = PosixExit.Code(siCode, *(int*)(info + PosixSpawnNative.SiStatusOffset));
                    break;
                }

                // macOS answers a stop too, WEXITED or not (measured on 15.7: a waitid blocked when the tree walk's SIGSTOP lands
                // returns CLD_STOPPED, and again each call until the exit). Ask again, backing off so a child left stopped costs little.
                Thread.Sleep(pause);
                pause = Math.Min(pause * 2, 250);
                continue;
            }

            int errno = Marshal.GetLastPInvokeError();
            if (errno != PosixSpawnNative.Eintr)
            {
                DiagnosticLog.Debug(ShellKinds.Category, $"pid {_pid.ToString(CultureInfo.InvariantCulture)}: waitid failed (errno {errno.ToString(CultureInfo.InvariantCulture)}); reaped elsewhere");
                lock (_lock)
                {
                    _reapedElsewhere = true;
                }

                code = -1;
                break;
            }
        }

        lock (_lock)
        {
            _exitCode = code;
            _exitSeen = true;
            if (_disposed || !_tiedToApp || PosixSpawnNative.KillPg(_pid, 0) != 0)
            {
                // Nothing the dispose would kill: no zombie kept.
                Reap();
            }
        }

        _exited.TrySetResult();
    }

    public override void Kill()
    {
        lock (_lock)
        {
            if (_reaped || _reapedElsewhere)
            {
                // The pid may be someone else's by now.
                return;
            }

            if (!_exitSeen)
            {
                // The walk first: once the leader dies its children are launchd's and the walk finds none of them.
                try
                {
                    using var leader = Process.GetProcessById(_pid);
                    leader.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Gone the instant before: the group kill still finds what it left.
                }
            }

            KillGroup();
        }
    }

    /// <summary><c>killpg</c> while the leader is unreaped (its group id cannot have been reused) and not reaped by someone else. Under the lock.</summary>
    private void KillGroup()
    {
        if (!_reaped && !_reapedElsewhere && PosixSpawnNative.KillPg(_pid, PosixSpawnNative.SigKill) != 0)
        {
            // ESRCH: every member already gone.
            DiagnosticLog.Debug(ShellKinds.Category, $"pid {_pid.ToString(CultureInfo.InvariantCulture)}: killpg errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Reaps the zombie; under the lock, after the exit is known.</summary>
    private void Reap()
    {
        if (_reaped || _reapedElsewhere)
        {
            return;
        }

        int status;
        while (PosixSpawnNative.WaitPid(_pid, &status, 0) < 0 && Marshal.GetLastPInvokeError() == PosixSpawnNative.Eintr)
        {
        }

        _reaped = true;
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_tiedToApp)
            {
                // A background command's leftovers, even after the leader's exit (Windows' kill-on-close job).
                KillGroup();
            }

            if (_exitSeen)
            {
                Reap();
            }
        }

        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _stdin.Dispose();
        _stdout.Dispose();
        _stderr.Dispose();
    }
}
