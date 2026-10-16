using NeonSidekick.Computers;

namespace NeonSidekick.Tasks;

/// <summary>
/// One change to a task (2026-10-15, the admin-tools plan), the model's (<c>task_control</c>) and the pane's alike: the task and its definition
/// read, <see cref="TaskAccess.WriteRefusal"/>'s order, the question — a run's naming the principal, the run level and every command line, since a
/// run is a privilege boundary — then the change waited out and audited. Only IRegisteredTask's Run, Stop and put_Enabled are ever called.
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

        var read = await access.ReadAsync(source, path, session => session.Detail(path), cancellationToken).ConfigureAwait(false);
        if (read.Error is { } failed)
        {
            return failed;
        }

        var detail = read.Value!;
        TaskDefinition? definition = null;
        try
        {
            definition = TaskXml.Parse(detail.Xml);
        }
        catch (TaskXmlException)
        {
            // An unreadable definition counts as privileged below (the safe reading).
            definition = new TaskDefinition(null, null, null, new TaskPrincipalInfo("SYSTEM", null, null, "HighestAvailable"), [], [], false, null, null);
        }

        if (TaskAccess.WriteRefusal(source.Computer, detail.Entry.Path, action, definition, writesOn) is { } refused)
        {
            return refused;
        }

        if (confirm is null)
        {
            return ComputerText.NoPaneToAsk;
        }

        string principal = TaskRender.PrincipalWords(definition.Principal);
        string question = action == TaskActions.Run
            ? TaskText.ConfirmRun(detail.Entry.Path, source.Where, principal, TaskRender.RunLevel(definition.Principal), TaskRender.Commands(definition))
            : TaskText.ConfirmChange(action, detail.Entry.Path, source.Where, principal);
        if (await confirm(question, cancellationToken).ConfigureAwait(false) is not true)
        {
            TaskAccess.Audit(byUser, action, path, source, "declined");
            return ComputerText.Declined;
        }

        string taskPath = detail.Entry.Path;
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
            TaskAccess.Audit(byUser, action, path, source, "failed: " + error);
            return error;
        }

        var state = result.Value!.State;
        TaskAccess.Audit(byUser, action, path, source, TaskText.StateWord(state));
        return cancellationToken.IsCancellationRequested ? ComputerText.StoppedWaiting : TaskText.Done(action, taskPath, source.Where, state);
    }
}
