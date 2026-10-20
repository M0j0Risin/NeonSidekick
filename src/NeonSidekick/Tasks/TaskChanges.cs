using NeonSidekick.Computers;

namespace NeonSidekick.Tasks;

/// <summary>
/// One change to a task (2026-10-15, the admin-tools plan), the model's (<c>task_control</c>) and the pane's alike: the task and its definition
/// read, <see cref="TaskAccess.WriteRefusal"/>'s order, the question — a run's naming the principal, the run level and every command line, since a
/// run is a privilege boundary — then the change waited out and audited. Only IRegisteredTask's Run, Stop and put_Enabled are ever called.
/// Since 2026-10-20 (a review): the principal judged against the session's own account, a model's run whose commands would not show whole in
/// the question refused, an unreadable definition's commands said to be unknown, and a change made but not read back said and audited so.
/// </summary>
public static class TaskChanges
{
    public static async Task<string> RunAsync(TaskAccess access, TaskSource source, string path, string action, bool writesOn, bool byUser,
        Func<string, CancellationToken, Task<bool?>>? confirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(path);
        if (TaskAccess.WriteRefusal(source.Computer, path, action, null, writesOn) is { } early)
        {
            return early;
        }

        var read = await access.ReadAsync(source, path, session => (Detail: session.Detail(path), session.Self), cancellationToken).ConfigureAwait(false);
        if (read.Error is { } failed)
        {
            return failed;
        }

        var (detail, self) = read.Value;
        TaskDefinition? readable = null;
        try
        {
            readable = TaskXml.Parse(detail.Xml);
        }
        catch (TaskXmlException)
        {
            // An unreadable definition counts as privileged below (the safe reading).
        }

        var definition = readable ?? new TaskDefinition(null, null, null, new TaskPrincipalInfo("SYSTEM", null, null, "HighestAvailable"), [], [], false, null, null);
        string taskPath = detail.Entry.Path;
        if (TaskAccess.WriteRefusal(source.Computer, taskPath, action, definition, writesOn, self) is { } refused)
        {
            return refused;
        }

        if (confirm is null)
        {
            return ComputerText.NoPaneToAsk;
        }

        string principal = readable is null ? TaskText.PrincipalUnknown : TaskRender.PrincipalWords(definition.Principal);
        string commands = TaskRender.Commands(readable);
        if (!byUser && action == TaskActions.Run && commands.Length > ComputerText.MaxShownInQuestion)
        {
            return TaskText.TooLongToAsk(taskPath, commands.Length);
        }

        string question = action == TaskActions.Run
            ? TaskText.ConfirmRun(taskPath, source.Where, principal, readable is null ? TaskText.RunLevelUnknown : TaskRender.RunLevel(definition.Principal), commands)
            : TaskText.ConfirmChange(action, taskPath, source.Where, principal);
        if (await confirm(question, cancellationToken).ConfigureAwait(false) is not true)
        {
            TaskAccess.Audit(byUser, action, taskPath, source, "declined");
            return ComputerText.Declined;
        }

        var result = await access.ChangeAsync(source, taskPath, session =>
        {
            switch (action)
            {
                case TaskActions.Run:
                    session.Run(taskPath);
                    break;
                case TaskActions.End:
                    session.End(taskPath);
                    break;
                default:
                    session.SetEnabled(taskPath, action == TaskActions.Enable);
                    break;
            }
        }).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            TaskAccess.Audit(byUser, action, taskPath, source, "failed: " + error);
            return error;
        }

        if (result.Value is not { } entry)
        {
            TaskAccess.Audit(byUser, action, taskPath, source, "done, the task not read after");
            return TaskText.DoneUnread(action, taskPath, source.Where);
        }

        TaskAccess.Audit(byUser, action, taskPath, source, TaskText.StateWord(entry.State));
        return cancellationToken.IsCancellationRequested ? ComputerText.StoppedWaiting : TaskText.Done(action, taskPath, source.Where, entry.State);
    }
}
