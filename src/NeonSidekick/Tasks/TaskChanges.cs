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

        var definition = readable ?? new TaskDefinition(null, null, null, new TaskPrincipalInfo(null, null, null, null), [], [], false, null, null);
        string taskPath = detail.Entry.Path;
        if (TaskAccess.WriteRefusal(source.Computer, taskPath, action, readable, writesOn, self, principalUnknown: readable is null) is { } refused)
        {
            return refused;
        }

        if (confirm is null)
        {
            return ComputerText.NoPaneToAsk;
        }

        string principal = readable is null ? TaskText.PrincipalUnknown : TaskRender.PrincipalWords(definition.Principal);
        string commands = TaskRender.Commands(readable);
        // The principal counts too (2026-10-23, the admin review's B6): a long one pushed the question past what the pane shows whole.
        // An enable shows what it lets run too (2026-10-23, the admin review's L2): its next trigger runs those commands.
        bool showsCommands = action is TaskActions.Run or TaskActions.Enable;
        int shown = (showsCommands ? commands.Length : 0) + principal.Length;
        if (!byUser && shown > ComputerText.MaxShownInQuestion)
        {
            return TaskText.TooLongToAsk(taskPath, shown);
        }

        string question = action == TaskActions.Run
            ? TaskText.ConfirmRun(taskPath, source.Where, principal, readable is null ? TaskText.RunLevelUnknown : TaskRender.RunLevel(definition.Principal), commands)
            : TaskText.ConfirmChange(action, taskPath, source.Where, principal, action == TaskActions.Enable ? commands : null);
        switch (await confirm(question, cancellationToken).ConfigureAwait(false))
        {
            case null:
                // Nobody was there to answer (2026-10-23, the admin review's G15): never told as the user's no.
                TaskAccess.Audit(byUser, action, taskPath, source, "not asked: nobody to answer");
                return ComputerText.NoPaneToAsk;
            case false:
                TaskAccess.Audit(byUser, action, taskPath, source, "declined");
                return ComputerText.Declined;
        }

        var result = await access.ChangeAsync(source, taskPath, session =>
        {
            // The definition the question showed is the one changed (2026-10-23, the admin review's C2: the change ran in a new session after the
            // yes with no re-check, so a task re-registered while the pane waited ran another command as another account).
            if (!string.Equals(session.Detail(taskPath).Xml, detail.Xml, StringComparison.Ordinal))
            {
                throw new TaskException(TaskErrors.ChangedSinceAsked, "the definition changed after the question");
            }

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
        }, cancellationToken).ConfigureAwait(false);
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

        // ChangeAsync returns only once the change is made (2026-10-23, the admin review's T13: a cancel noticed after it answered "may still
        // complete", and a model ran a side-effecting task again).
        TaskAccess.Audit(byUser, action, taskPath, source, TaskText.StateWord(entry.State));
        return TaskText.Done(action, taskPath, source.Where, entry.State);
    }
}
