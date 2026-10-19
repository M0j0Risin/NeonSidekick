using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>A child <see cref="ChildJob.StartPiped"/> started: the process, its own job (0 for none), and the app's ends of its stdin, stdout and stderr pipes.</summary>
internal sealed record PipedChild(Process Process, nint Job, SafeFileHandle Input, SafeFileHandle Output, SafeFileHandle Error);

public static partial class ChildJob
{
    /// <summary>
    /// Starts a shell command inside a job of its own from its first instruction (2026-10-19, the third shell review: <c>ShellRunner</c>
    /// put the child in its job only after <c>Process.Start</c> returned it running, so what a background <c>cmd</c> line started in that
    /// instant — <c>chcp</c>, an inner <c>cmd</c>, a detached server — ran outside the kill-on-close job and outlived the app's exit or
    /// crash). <see cref="Start(string, IReadOnlyList{string}, string, out bool, out string)"/>'s shape with three pipes: made private,
    /// the child's ends inheritable only under .NET's process-start lock and named in the handle list as the only handles it takes;
    /// <c>CreateProcessW</c> suspended with a hidden console (<c>CREATE_NO_WINDOW</c>, as <c>Process.Start</c>'s <c>CreateNoWindow</c>) and
    /// <paramref name="environment"/> as its block; the job (kill-on-close with <paramref name="killOnClose"/>) assigned; then let run.
    /// Still <c>ShellRunner</c>'s one process-start site, moved here as the headless browser's was. <paramref name="executable"/> is a full
    /// path. Null with an empty <paramref name="error"/> when .NET's lock cannot be reached (the caller starts it as before, with
    /// <c>Process.Start</c>); null with <paramref name="error"/> Windows' reason when it could not start. A job that could not be made or
    /// assigned leaves it running outside one (logged), the record's <see cref="PipedChild.Job"/> 0.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe PipedChild? StartPiped(string executable, string commandLine, string? workingDirectory, IDictionary<string, string?> environment, bool killOnClose, out string error)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(environment);
        error = "";
        object? processStart = ProcessStartLock();
        if (processStart is null)
        {
            return null;
        }

        var (job, jobError) = Create(killOnClose);
        if (job == 0)
        {
            DiagnosticLog.Warn(Category, "No job object for the command: " + jobError);
        }

        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null, errRead = null, errWrite = null;
        nint list = 0;
        bool listMade = false;
        bool locked = false;
        bool handedOver = false;
        nint* handles = stackalloc nint[3];
        new Span<nint>(handles, 3).Clear();
        try
        {
            if (!JobObjectNative.CreatePipe(out inRead, out inWrite, 0, 0)
                || !JobObjectNative.CreatePipe(out outRead, out outWrite, 0, 0)
                || !JobObjectNative.CreatePipe(out errRead, out errWrite, 0, 0))
            {
                error = Win32Message();
                return null;
            }

            handles[0] = inRead.DangerousGetHandle();
            handles[1] = outWrite.DangerousGetHandle();
            handles[2] = errWrite.DangerousGetHandle();
            char[] block = EnvironmentBlock(environment);
            char[] line = (commandLine + "\0").ToCharArray();   // CreateProcessW may write to it

            // Inheritable only while the lock is held, private again before it is let go: no Process.Start elsewhere takes them.
            Monitor.Enter(processStart, ref locked);
            for (int i = 0; i < 3; i++)
            {
                if (!JobObjectNative.SetHandleInformation(handles[i], JobObjectNative.HandleFlagInherit, JobObjectNative.HandleFlagInherit))
                {
                    error = Win32Message();
                    return null;
                }
            }

            nint size = 0;
            JobObjectNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);   // fails, saying how big the list is
            list = Marshal.AllocHGlobal(size);
            listMade = JobObjectNative.InitializeProcThreadAttributeList(list, 1, 0, ref size);
            if (!listMade || !JobObjectNative.UpdateProcThreadAttribute(list, 0, JobObjectNative.ProcThreadAttributeHandleList, (nint)handles, 3 * sizeof(nint), 0, 0))
            {
                error = Win32Message();
                return null;
            }

            var startup = new JobObjectNative.StartupInfoEx { AttributeList = list };
            startup.StartupInfo.Size = (uint)sizeof(JobObjectNative.StartupInfoEx);
            startup.StartupInfo.Flags = JobObjectNative.StartfUseStdHandles;
            startup.StartupInfo.StdInput = handles[0];
            startup.StartupInfo.StdOutput = handles[1];
            startup.StartupInfo.StdError = handles[2];
            JobObjectNative.ProcessInformation started;
            bool made;
            fixed (char* text = line)
            fixed (char* env = block)
            {
                made = JobObjectNative.CreateProcess(
                    executable, text, 0, 0, true,
                    JobObjectNative.CreateSuspended | JobObjectNative.CreateNoWindow | JobObjectNative.ExtendedStartupInfoPresent | JobObjectNative.CreateUnicodeEnvironment,
                    (nint)env, string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory, ref startup, out started);
            }

            if (!made)
            {
                error = Win32Message();
                return null;
            }

            MakePrivate(handles);
            Monitor.Exit(processStart);
            locked = false;

            var process = Launch(job, started, out bool inJob, out error);
            if (process is null)
            {
                return null;
            }

            if (!inJob && job != 0)
            {
                Close(job);
                job = 0;
            }

            handedOver = true;
            return new PipedChild(process, job, inWrite, outRead, errRead);
        }
        finally
        {
            if (locked)
            {
                MakePrivate(handles);
                Monitor.Exit(processStart);
            }

            if (listMade)
            {
                JobObjectNative.DeleteProcThreadAttributeList(list);
            }

            if (list != 0)
            {
                Marshal.FreeHGlobal(list);
            }

            // The child's ends always go (it holds its own copies); the app's only when nothing was handed over.
            inRead?.Dispose();
            outWrite?.Dispose();
            errWrite?.Dispose();
            if (!handedOver)
            {
                inWrite?.Dispose();
                outRead?.Dispose();
                errRead?.Dispose();
                Close(job);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void MakePrivate(nint* handles)
    {
        for (int i = 0; i < 3; i++)
        {
            if (handles[i] != 0)
            {
                JobObjectNative.SetHandleInformation(handles[i], JobObjectNative.HandleFlagInherit, 0);
            }
        }
    }

    private static string Win32Message() => new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message;

    /// <summary>
    /// <paramref name="environment"/> as <c>CreateProcessW</c>'s UTF-16 block, the way <c>Process.Start</c> writes it: each
    /// <c>NAME=value</c> ended by a NUL, sorted ordinal and case-blind, the block ended by one more. Pure.
    /// </summary>
    internal static char[] EnvironmentBlock(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var names = environment.Keys.ToArray();
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        var block = new StringBuilder();
        foreach (string name in names)
        {
            block.Append(name).Append('=').Append(environment[name]).Append('\0');
        }

        if (block.Length == 0)
        {
            block.Append('\0');
        }

        return block.Append('\0').ToString().ToCharArray();
    }
}
