using System.Globalization;

namespace NeonSidekick.Tasks;

/// <summary>Where a failure happened, for its sentence.</summary>
public sealed record TaskErrorContext(string Computer, string? Task, bool Remote, string Account, bool Write);

/// <summary>
/// An HRESULT from the Task Scheduler as a sentence (2026-10-15, the admin-tools plan). Access denied is worded per target: locally a run or an
/// end of a system task needs the app run as administrator (it never elevates itself); remotely the account needs rights there and UAC filters a
/// local administrator's network sign-in. A missing task (0x80070002) names <c>task_list</c>. Pure.
/// </summary>
public static class TaskErrors
{
    /// <summary>The app's own code (customer bit set) for a task whose definition changed between the question and the change (C2).</summary>
    public const int ChangedSinceAsked = unchecked((int)0xA0D10001);

    public static string Sentence(int code, TaskErrorContext context, string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        string task = context.Task ?? "that task";
        return unchecked((uint)code) switch
        {
            0x80070005 when context.Remote => $"Error: {context.Computer} refused {context.Account} access to its scheduled tasks. The account needs rights there, and a local administrator's sign-in over the network is filtered by UAC's remote restrictions; a domain account in the Administrators group works.",
            0x80070005 when context.Write => $"Error: Windows refused the change to {task}: it needs the app run as administrator, or a task whose permissions allow you.",
            0x80070005 => $"Error: Windows refused access to {task} on {context.Computer}.",
            0x80070002 or 0x80070003 => $"Error: there is no task or folder named '{task}' on {context.Computer}; task_list lists them.",
            0x800706BA or 0x80070035 => $"Error: {context.Computer} did not answer the Task Scheduler's call. Check the name, and that the Remote Scheduled Tasks Management firewall rule is on there.",
            0x8007052E => $"Error: {context.Computer} refused the sign-in as {context.Account}: the user name or password is wrong.",
            0x8004131F => $"Error: {task} is already running.",
            0x80041326 => $"Error: {task} is disabled; enable it before running it.",
            // Run's own refusals (2026-10-23, the admin review's T18): opaque hex to the model and the audit before.
            0x80041328 => $"Error: {task}'s settings do not allow it to be run on demand.",
            0x80041320 => $"Error: {task} runs only while its user is signed in, and that user is not signed in on {context.Computer}.",
            0x8004131E => $"Error: {task} is past its end boundary, so it will not run.",
            0x80041324 => $"Error: Windows has already tried to run {task}.",
            0xA0D10001 => $"Error: {task} on {context.Computer} changed after the question (its definition is not the one shown), so nothing was done; ask again.",
            _ => $"Error: the Task Scheduler on {context.Computer} failed (code 0x{unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture)})" + (detail.Length > 0 ? ": " + detail : "."),
        };
    }
}
