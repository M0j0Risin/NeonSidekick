using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// Ties a child process's life to the app's (2026-09-29, the embedded model's <c>llama-server</c>: gigabytes of VRAM that
/// must not outlive a crash). The app's other children rely on <c>Kill(entireProcessTree)</c> at dispose, which a
/// crashed, killed or <c>Environment.FailFast</c>ed process never reaches; a Windows job object with
/// <c>KILL_ON_JOB_CLOSE</c> does not need the app's help — the kernel closes the job's one handle when the process
/// ends, however it ends, and kills everything in the job.
///
/// <para>One job for the whole app, created on first use and never closed by hand (closing it is what kills). Nested
/// jobs are fine since Windows 8, so a terminal that runs the app inside its own job does not stop the assignment.
/// A failure is logged and the child simply runs unjobbed — no worse than every other child. A no-op off Windows.</para>
///
/// <para>Two ways in: <see cref="Assign"/> for a process already started (<c>llama-server</c>, the Claude Code server: what they start
/// comes later, born in the job), and <see cref="Start"/> (2026-10-11, code review of the headless browser), which starts one suspended inside
/// the job so nothing it ever starts is outside it.</para>
/// </summary>
public static class ChildJob
{
    private const string Category = "EmbeddedLlm";
    private static readonly Lock Gate = new();
    private static nint _job;

    /// <summary>Puts <paramref name="process"/> in the app's kill-on-close job. False (logged) when that could not be done.</summary>
    public static bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (Gate)
        {
            nint job = Job();
            return job != 0 && AssignTo(job, process);
        }
    }

    /// <summary>
    /// Starts <paramref name="executable"/> with <paramref name="arguments"/> inside the app's kill-on-close job from its first
    /// instruction (2026-10-11, code review of the headless browser: <c>Process.Start</c> hands back a process already running, so a
    /// child it started before the assignment — Edge's crash handler, a copy relaunched of itself — was outside the job, outliving
    /// the app holding the profile folder; a snapshot swept in after still missed what those started in turn). Created suspended,
    /// assigned, then let run: every process it ever starts is born in the job. No window; stdin and stdout on <c>NUL</c>, stderr
    /// into <paramref name="stderrPath"/> (a file, so a handle some other start inherits in the moment they are inheritable holds
    /// up no pipe's end), and those the only handles it inherits (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, so it takes none of
    /// the app's). Null with <paramref name="error"/> when it could not start; <paramref name="inJob"/> false (logged) when it runs
    /// outside the job. The headless browser's own process-start site, moved here from its <c>Process.Start</c>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static Process? Start(string executable, IReadOnlyList<string> arguments, string stderrPath, out bool inJob, out string error)
    {
        lock (Gate)
        {
            return Start(Job(), executable, arguments, stderrPath, out inJob, out error);
        }
    }

    /// <summary><see cref="Start(string, IReadOnlyList{string}, string, out bool, out string)"/> into <paramref name="job"/> (0 = none; the tests' own, so closing it shows who was in it).</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe Process? Start(nint job, string executable, IReadOnlyList<string> arguments, string stderrPath, out bool inJob, out string error)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(stderrPath);
        inJob = false;
        SafeFileHandle nul;
        SafeFileHandle log;
        try
        {
            nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "no NUL device (" + ex.Message + ")";
            return null;
        }

        try
        {
            log = File.OpenHandle(stderrPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            nul.Dispose();
            error = "could not create " + stderrPath + " (" + ex.Message + ")";
            return null;
        }

        nint list = 0;
        bool listMade = false;
        try
        {
            nint* handles = stackalloc nint[2];
            handles[0] = nul.DangerousGetHandle();
            handles[1] = log.DangerousGetHandle();
            if (!JobObjectNative.SetHandleInformation(handles[0], JobObjectNative.HandleFlagInherit, JobObjectNative.HandleFlagInherit)
                || !JobObjectNative.SetHandleInformation(handles[1], JobObjectNative.HandleFlagInherit, JobObjectNative.HandleFlagInherit))
            {
                error = LastError("SetHandleInformation");
                return null;
            }

            nint size = 0;
            JobObjectNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);   // fails, saying how big the list is
            list = Marshal.AllocHGlobal(size);
            listMade = JobObjectNative.InitializeProcThreadAttributeList(list, 1, 0, ref size);
            if (!listMade || !JobObjectNative.UpdateProcThreadAttribute(list, 0, JobObjectNative.ProcThreadAttributeHandleList, (nint)handles, 2 * sizeof(nint), 0, 0))
            {
                error = LastError(listMade ? "UpdateProcThreadAttribute" : "InitializeProcThreadAttributeList");
                return null;
            }

            var startup = new JobObjectNative.StartupInfoEx { AttributeList = list };
            startup.StartupInfo.Size = (uint)sizeof(JobObjectNative.StartupInfoEx);
            startup.StartupInfo.Flags = JobObjectNative.StartfUseStdHandles;
            startup.StartupInfo.StdInput = handles[0];
            startup.StartupInfo.StdOutput = handles[0];
            startup.StartupInfo.StdError = handles[1];
            char[] line = (CommandLine(executable, arguments) + "\0").ToCharArray();   // CreateProcessW may write to it
            JobObjectNative.ProcessInformation started;
            bool made;
            fixed (char* text = line)
            {
                made = JobObjectNative.CreateProcess(executable, text, 0, 0, true, JobObjectNative.CreateSuspended | JobObjectNative.CreateNoWindow | JobObjectNative.ExtendedStartupInfoPresent, 0, null, ref startup, out started);
            }

            if (!made)
            {
                error = LastError("CreateProcess");
                return null;
            }

            return Launch(job, started, out inJob, out error);
        }
        finally
        {
            if (listMade)
            {
                JobObjectNative.DeleteProcThreadAttributeList(list);
            }

            if (list != 0)
            {
                Marshal.FreeHGlobal(list);
            }

            nul.Dispose();
            log.Dispose();
        }
    }

    /// <summary>The suspended <paramref name="started"/> put in <paramref name="job"/>, held as a <see cref="Process"/> and let run; killed and null when it cannot be held.</summary>
    [SupportedOSPlatform("windows")]
    private static Process? Launch(nint job, JobObjectNative.ProcessInformation started, out bool inJob, out string error)
    {
        error = "";
        inJob = false;
        try
        {
            if (job != 0)
            {
                inJob = JobObjectNative.AssignProcessToJobObject(job, started.Process);
                if (!inJob)
                {
                    DiagnosticLog.Warn(Category, "The child process runs outside the job object: " + LastError("AssignProcessToJobObject"));
                }
            }

            Process process;
            try
            {
                process = Process.GetProcessById((int)started.ProcessId);
                _ = process.Handle;   // opened while it cannot have exited, so its exit code and the waits hold after it ends
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                JobObjectNative.TerminateProcess(started.Process, 1);
                error = "could not hold the started process (" + ex.Message + ")";
                return null;
            }

            if (JobObjectNative.ResumeThread(started.Thread) == uint.MaxValue)
            {
                error = LastError("ResumeThread");
                JobObjectNative.TerminateProcess(started.Process, 1);
                process.Dispose();
                return null;
            }

            return process;
        }
        finally
        {
            JobObjectNative.CloseHandle(started.Thread);
            JobObjectNative.CloseHandle(started.Process);
        }
    }

    /// <summary>
    /// The command line for <paramref name="executable"/> and <paramref name="arguments"/> as <c>ProcessStartInfo.ArgumentList</c> writes
    /// it: the executable quoted, each argument as the C runtime splits it back — quoted when blank or holding a space or a quote,
    /// a quote escaped, the backslashes before a quote or the closing one doubled. Pure.
    /// </summary>
    internal static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var line = new StringBuilder().Append('"').Append(executable).Append('"');
        foreach (string argument in arguments)
        {
            line.Append(' ');
            if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                line.Append(argument);
                continue;
            }

            line.Append('"');
            for (int i = 0; i < argument.Length; i++)
            {
                int slashes = 0;
                while (i < argument.Length && argument[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    line.Append('\\', slashes * 2);
                }
                else if (argument[i] == '"')
                {
                    line.Append('\\', (slashes * 2) + 1).Append('"');
                }
                else
                {
                    line.Append('\\', slashes).Append(argument[i]);
                }
            }

            line.Append('"');
        }

        return line.ToString();
    }

    /// <summary>
    /// The attribute list <see cref="Start(string, IReadOnlyList{string}, string, out bool, out string)"/> hands <c>CreateProcessW</c>,
    /// made, given a handle list and deleted — the smoke check's proof that the calls resolve and take their sizes under AOT, no
    /// process started. False and why when a step fails, and always off Windows.
    /// </summary>
    internal static unsafe (bool Ok, string Error) ProbeAttributeList()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, "not Windows");
        }

        using var nul = File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        nint handle = nul.DangerousGetHandle();
        nint size = 0;
        JobObjectNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size <= 0)
        {
            return (false, LastError("InitializeProcThreadAttributeList (size)"));
        }

        nint list = Marshal.AllocHGlobal(size);
        try
        {
            if (!JobObjectNative.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                return (false, LastError("InitializeProcThreadAttributeList"));
            }

            bool updated = JobObjectNative.UpdateProcThreadAttribute(list, 0, JobObjectNative.ProcThreadAttributeHandleList, (nint)(&handle), sizeof(nint), 0, 0);
            string why = updated ? "" : LastError("UpdateProcThreadAttribute");
            JobObjectNative.DeleteProcThreadAttributeList(list);
            return (updated, why);
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary>The app's job, made on first use; 0 (logged) when it cannot be. The caller holds <see cref="Gate"/>.</summary>
    private static nint Job()
    {
        if (_job == 0)
        {
            var (job, error) = CreateKillOnClose();
            if (job == 0)
            {
                DiagnosticLog.Warn(Category, "No job object for child processes: " + error);
                return 0;
            }

            _job = job;
        }

        return _job;
    }

    /// <summary>A new job that kills its processes when its last handle closes, or 0 and why not.</summary>
    internal static (nint Job, string Error) CreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (0, "not Windows");
        }

        nint job = JobObjectNative.CreateJobObject(0, null);
        if (job == 0)
        {
            return (0, LastError("CreateJobObject"));
        }

        var info = new JobObjectNative.JobObjectExtendedLimitInformationStruct();
        info.BasicLimitInformation.LimitFlags = JobObjectNative.JobObjectLimitKillOnJobClose;
        if (!JobObjectNative.SetInformationJobObject(job, JobObjectNative.JobObjectExtendedLimitInformation, ref info, (uint)Unsafe.SizeOf<JobObjectNative.JobObjectExtendedLimitInformationStruct>()))
        {
            string error = LastError("SetInformationJobObject");
            JobObjectNative.CloseHandle(job);
            return (0, error);
        }

        return (job, "");
    }

    /// <summary>Puts <paramref name="process"/> in <paramref name="job"/>; false (logged) on failure.</summary>
    internal static bool AssignTo(nint job, Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string why;
        try
        {
            if (JobObjectNative.AssignProcessToJobObject(job, process.Handle))
            {
                return true;
            }

            why = LastError("AssignProcessToJobObject");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            why = ex.Message;
        }

        // Gone already, it runs nowhere: a debug line, not a warning that it runs outside (code review, 2026-10-11).
        if (HasExited(process))
        {
            DiagnosticLog.Debug(Category, "The child process exited before it could join the job object: " + why);
        }
        else
        {
            DiagnosticLog.Warn(Category, "The child process runs outside the job object: " + why);
        }

        return false;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The limit flags <paramref name="job"/> carries, read back — the smoke check's proof that the struct crossed intact.</summary>
    internal static (bool Ok, uint Flags, string Error) QueryFlags(nint job)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, 0, "not Windows");
        }

        var info = new JobObjectNative.JobObjectExtendedLimitInformationStruct();
        uint size = (uint)Unsafe.SizeOf<JobObjectNative.JobObjectExtendedLimitInformationStruct>();
        if (!JobObjectNative.QueryInformationJobObject(job, JobObjectNative.JobObjectExtendedLimitInformation, ref info, size, out uint returned))
        {
            return (false, 0, LastError("QueryInformationJobObject"));
        }

        return returned == size ? (true, info.BasicLimitInformation.LimitFlags, "") : (false, 0, string.Create(CultureInfo.InvariantCulture, $"returned {returned} bytes, expected {size}"));
    }

    /// <summary>Closes a job handle made by <see cref="CreateKillOnClose"/> — which kills what is in it.</summary>
    internal static void Close(nint job)
    {
        if (OperatingSystem.IsWindows() && job != 0)
        {
            JobObjectNative.CloseHandle(job);
        }
    }

    private static string LastError(string call) =>
        string.Create(CultureInfo.InvariantCulture, $"{call} failed (error {Marshal.GetLastPInvokeError()})");
}
