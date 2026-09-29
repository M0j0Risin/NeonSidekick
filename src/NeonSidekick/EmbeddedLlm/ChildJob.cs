using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
            if (_job == 0)
            {
                var (job, error) = CreateKillOnClose();
                if (job == 0)
                {
                    DiagnosticLog.Warn(Category, "No job object for child processes: " + error);
                    return false;
                }

                _job = job;
            }

            return AssignTo(_job, process);
        }
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

        try
        {
            if (JobObjectNative.AssignProcessToJobObject(job, process.Handle))
            {
                return true;
            }

            DiagnosticLog.Warn(Category, "The child process runs outside the job object: " + LastError("AssignProcessToJobObject"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Warn(Category, "The child process runs outside the job object: " + ex.Message);
        }

        return false;
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
