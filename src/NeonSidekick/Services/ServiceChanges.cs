using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Services;

/// <summary>
/// One change to a service (2026-10-15, the admin-tools plan), the model's (<c>service_control</c>, <c>service_start_type</c>) and the pane's
/// alike: the service read, <see cref="ServiceAccess.WriteRefusal"/>'s order, the running dependents of a stop or restart (error 1051's case,
/// named), the question on the pane — the action, the service, the computer, the account and the full image path — then the change waited
/// out and audited. <paramref name="writesOn"/> is <c>Service writes</c> for the model and true for the pane (its own hand skips only that);
/// <paramref name="confirm"/> null means nobody to ask (headless, a bot), a refusal. The answer's sentence either way. Since 2026-10-20 (a
/// review): the running dependents name only the visible ones, a model's change whose program line would not show whole in the question is
/// refused, a restart whose stop did not end is not started, and a change made but not read back, or made in part, is said and audited so.
/// </summary>
public static class ServiceChanges
{
    public static async Task<string> RunAsync(ServiceAccess access, ServiceSource source, string name, string action, ServiceStartChoice? choice, bool writesOn, bool byUser,
        Func<string, CancellationToken, Task<bool?>>? confirm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        if (ServiceAccess.WriteRefusal(source.Computer, name, action, null, writesOn) is { } early)
        {
            return early;
        }

        var read = await access.ReadAsync(source, name, session => session.Detail(name), cancellationToken).ConfigureAwait(false);
        if (read.Error is { } failed)
        {
            return failed;
        }

        var detail = read.Value!;
        if (ServiceAccess.WriteRefusal(source.Computer, detail.Config.Name, action, detail.Config, writesOn) is { } refused)
        {
            return refused;
        }

        if (action is ServiceActions.Stop or ServiceActions.Restart)
        {
            var running = detail.Dependents.Where(d => d.State != ServiceRunState.Stopped).Select(d => d.Name).ToList();
            if (running.Count > 0)
            {
                var shown = running.Where(d => ServiceAccess.Visible(source.Computer, d)).ToList();
                return ServiceText.DependentsRunning(name, shown, running.Count - shown.Count);
            }
        }

        if (confirm is null)
        {
            return ComputerText.NoPaneToAsk;
        }

        if (!byUser && action != ServiceActions.StartType && UntrustedText.OneLine(detail.Config.ImagePath).Length > ComputerText.MaxShownInQuestion)
        {
            return ServiceText.TooLongToAsk(name, UntrustedText.OneLine(detail.Config.ImagePath).Length);
        }

        string question = action == ServiceActions.StartType
            ? ServiceText.ConfirmStartType(name, detail.Config.DisplayName, source.Where, source.Computer.Account, ServiceText.StartWord(detail.Config.StartType, detail.Config.DelayedAutoStart), ServiceText.ChoiceWord(choice!.Value))
            : ServiceText.ConfirmQuestion(action, name, detail.Config.DisplayName, source.Where, source.Computer.Account, detail.Config.ImagePath);
        if (await confirm(question, cancellationToken).ConfigureAwait(false) is not true)
        {
            ServiceAccess.Audit(byUser, action, name, source, "declined");
            return ComputerText.Declined;
        }

        bool notStarted = false;
        var result = action switch
        {
            ServiceActions.Start => await access.ChangeAsync(source, name, s => s.Start(name), Settled(ServiceRunState.Running), cancellationToken).ConfigureAwait(false),
            ServiceActions.Stop => await access.ChangeAsync(source, name, s => s.Stop(name), Settled(ServiceRunState.Stopped), cancellationToken).ConfigureAwait(false),
            ServiceActions.Restart => await access.ChangeAsync(source, name, s => notStarted = !Restart(s, name, detail.Status.State, cancellationToken), s => notStarted || Settled(ServiceRunState.Running)(s), cancellationToken).ConfigureAwait(false),
            _ => await access.ChangeAsync(source, name, s => s.SetStartType(name, choice!.Value), _ => true, cancellationToken).ConfigureAwait(false),
        };
        if (result.Error is { } error)
        {
            ServiceAccess.Audit(byUser, action, name, source, (result.Partial ? "partly done: " : "failed: ") + error);
            return error;
        }

        var status = result.Value!;
        if (notStarted)
        {
            ServiceAccess.Audit(byUser, action, name, source, "stopped only, not started again: " + ServiceText.StateWord(status.State));
            return ServiceText.RestartNotStarted(name, source.Where, status.State);
        }

        if (action != ServiceActions.StartType && status.State == ServiceRunState.Unknown)
        {
            ServiceAccess.Audit(byUser, action, name, source, "done, its state not read");
            return ServiceText.DoneUnread(action, name, source.Where);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ServiceAccess.Audit(byUser, action, name, source, "stopped waiting at " + ServiceText.StateWord(status.State));
            return ComputerText.StoppedWaiting;
        }

        string answer = action == ServiceActions.StartType ? ServiceText.StartTypeDone(name, source.Where, ServiceText.ChoiceWord(choice!.Value))
            : Settled(action == ServiceActions.Stop ? ServiceRunState.Stopped : ServiceRunState.Running)(status) && status.State is ServiceRunState.Running or ServiceRunState.Stopped
                ? ServiceText.Done(action, name, source.Where, status.State)
                : ServiceText.StillPending(action, name, source.Where, status.State);
        ServiceAccess.Audit(byUser, action, name, source, ServiceText.StateWord(status.State));
        return answer;
    }

    /// <summary>The wait's end: the wanted state, or a stop when a start failed (the service gave up).</summary>
    private static Func<ServiceStatus, bool> Settled(ServiceRunState wanted) => status =>
        status.State == wanted || (wanted == ServiceRunState.Running && status.State == ServiceRunState.Stopped && status.CheckPoint == 0 && status.Win32ExitCode is not 0);

    /// <summary>
    /// A restart inside one session: the stop (when running), its stopped state waited for (60 s at most), then the start. False, the start not
    /// tried, when the stop did not end in time or the wait was stopped (2026-10-20, a review: a start sent to a service still stopping failed
    /// with 1056 and left it down, reported as a failed restart).
    /// </summary>
    private static bool Restart(IServiceSession session, string name, ServiceRunState was, CancellationToken cancellationToken)
    {
        if (was != ServiceRunState.Stopped)
        {
            session.Stop(name);
            bool stopped = false;
            for (int i = 0; i < 240 && !cancellationToken.IsCancellationRequested; i++)
            {
                try
                {
                    if (session.Status(name).State == ServiceRunState.Stopped)
                    {
                        stopped = true;
                        break;
                    }
                }
                catch (ServiceException)
                {
                    break;   // the stop was made; a state that cannot be read is no stop seen, so no start
                }

                Thread.Sleep(250);
            }

            if (!stopped)
            {
                return false;
            }
        }

        session.Start(name);
        return true;
    }

    /// <summary>A start type word read; null for one that is none.</summary>
    public static ServiceStartChoice? Choice(string word) => word.Trim().ToLowerInvariant() switch
    {
        "automatic" or "auto" => ServiceStartChoice.Automatic,
        "delayed" or "delayed-auto" or "delayed_auto" => ServiceStartChoice.Delayed,
        "manual" or "demand" => ServiceStartChoice.Manual,
        "disabled" => ServiceStartChoice.Disabled,
        _ => null,
    };
}
