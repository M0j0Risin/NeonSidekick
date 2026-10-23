using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Services;

/// <summary>How a restart's stop ended when the start was not tried (2026-10-23, the admin review's T14: every case was "did not stop in time").</summary>
public enum RestartStop
{
    /// <summary>The stop ended: the start was tried.</summary>
    Stopped,

    /// <summary>The stop did not end within its wait hint's progress (or the longest wait).</summary>
    TimedOut,

    /// <summary>The user stopped waiting.</summary>
    Cancelled,

    /// <summary>The state could not be read after the stop.</summary>
    Unreadable,
}

/// <summary>
/// One change to a service (2026-10-15, the admin-tools plan), the model's (<c>service_control</c>, <c>service_start_type</c>) and the pane's
/// alike: the service read, <see cref="ServiceAccess.WriteRefusal"/>'s order, the running dependents of a stop or restart (error 1051's case,
/// named), the question on the pane — the action, the service, the computer, the account and the full image path — then the change waited
/// out and audited. <paramref name="writesOn"/> is <c>Service writes</c> for the model and true for the pane (its own hand skips only that);
/// <paramref name="confirm"/> null means nobody to ask (headless, a bot), a refusal. The answer's sentence either way. Since 2026-10-20 (a
/// review): the running dependents name only the visible ones, a model's change whose program line would not show whole in the question is
/// refused, a restart whose stop did not end is not started, and a change made but not read back, or made in part, is said and audited so.
/// <para>Since 2026-10-23 (the admin review): a start (or restart) also starts the service's stopped dependencies, so each passes the same
/// checks and is named in the question (M3); a start type set to disabled is refused while dependents run, and the question names those that
/// depend on it (M4); only a stop, a restart and a disable read the dependents (M6); the question says the account the service runs as (G3),
/// its program for a start type too (L2) and, for a shared <c>svchost</c> service, its DLL (T10); a start that ended stopped is a failed start
/// (G1); a restart whose start failed after its stop says the service is down (G2), decides its stop on the state read in the change's own
/// session (B13), waits for the stop by its wait hint and names why a start was not tried (T14); a start type's audit names the types (G4); a
/// question nobody answered is not a decline (G15).</para>
/// </summary>
public static class ServiceChanges
{
    /// <summary>The most stopped dependencies a start follows (each read once); past it, the start is refused.</summary>
    public const int MaxDependencies = 32;

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

        bool disables = action == ServiceActions.StartType && choice == ServiceStartChoice.Disabled;
        bool needDependents = action is ServiceActions.Stop or ServiceActions.Restart || disables;
        bool starts = action is ServiceActions.Start or ServiceActions.Restart;
        var read = await access.ReadAsync(source, name, session =>
        {
            var detail = session.Detail(name, needDependents);
            string? dll = IsShared(detail.Config.ImagePath) ? session.ServiceDll(detail.Config.Name) : null;
            var stoppedDependencies = starts ? StoppedDependencies(session, detail.Config) : [];
            return (Detail: detail, Dll: dll, Starts: stoppedDependencies);
        }, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } failed)
        {
            return failed;
        }

        var (detail, dll, alsoStarts) = read.Value;
        if (ServiceAccess.WriteRefusal(source.Computer, detail.Config.Name, action, detail.Config, writesOn) is { } refused)
        {
            return refused;
        }

        if (alsoStarts is null)
        {
            return ServiceText.TooManyDependencies(name, MaxDependencies);
        }

        foreach (var dependency in alsoStarts)
        {
            if (ServiceAccess.WriteRefusal(source.Computer, dependency.Name, ServiceActions.Start, dependency, writesOn) is { } dependencyRefused)
            {
                return ServiceText.DependencyRefused(name, dependency.Name, dependencyRefused);
            }
        }

        if (needDependents)
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

        string program = UntrustedText.OneLine(detail.Config.ImagePath);
        int shownLength = program.Length + (dll?.Length ?? 0) + alsoStarts.Sum(d => d.Name.Length + 2);
        if (!byUser && shownLength > ComputerText.MaxShownInQuestion)
        {
            return ServiceText.TooLongToAsk(name, shownLength);
        }

        string from = ServiceText.StartWord(detail.Config.StartType, detail.Config.DelayedAutoStart);
        string runsAs = detail.Config.Account;
        string? shared = IsShared(detail.Config.ImagePath) ? dll ?? ServiceText.DllUnread : null;
        var dependents = disables ? detail.Dependents.Where(d => ServiceAccess.Visible(source.Computer, d.Name)).Select(d => d.Name).ToList() : [];
        string question = action == ServiceActions.StartType
            ? ServiceText.ConfirmStartType(name, detail.Config.DisplayName, source.Where, runsAs, from, ServiceText.ChoiceWord(choice!.Value), detail.Config.ImagePath, shared, dependents)
            : ServiceText.ConfirmQuestion(action, name, detail.Config.DisplayName, source.Where, runsAs, detail.Config.ImagePath, shared, alsoStarts.Select(d => d.Name).ToList());
        string types = action == ServiceActions.StartType ? " (from " + from + " to " + ServiceText.ChoiceWord(choice!.Value) + ")" : "";
        switch (await confirm(question, cancellationToken).ConfigureAwait(false))
        {
            case null:
                // Nobody was there to answer (G15): never told as the user's no.
                ServiceAccess.Audit(byUser, action, name, source, "not asked: nobody to answer" + types);
                return ComputerText.NoPaneToAsk;
            case false:
                ServiceAccess.Audit(byUser, action, name, source, "declined" + types);
                return ComputerText.Declined;
        }

        // What the question showed is what is changed (2026-10-23, the admin review's C2): the program, the account and a shared service's DLL read
        // again in the change's own session; a service whose configuration moved since is not touched. A DLL is compared only when both reads
        // gave one (2026-10-23, the branch review of DP7): a Remote Registry read failing the second time was refused as a change.
        void Same(IServiceSession s)
        {
            var now = s.Detail(name, dependents: false).Config;
            if (!string.Equals(now.ImagePath, detail.Config.ImagePath, StringComparison.Ordinal) || !string.Equals(now.Account, detail.Config.Account, StringComparison.OrdinalIgnoreCase)
                || (IsShared(now.ImagePath) && dll is not null && s.ServiceDll(now.Name) is { } nowDll && !string.Equals(nowDll, dll, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ServiceException(ServiceErrors.ChangedSinceAsked, "the service changed after the question");
            }
        }

        var stop = RestartStop.Stopped;
        var result = action switch
        {
            ServiceActions.Start => await access.ChangeAsync(source, name, s => { Same(s); s.Start(name); }, Settled(ServiceRunState.Running), cancellationToken).ConfigureAwait(false),
            ServiceActions.Stop => await access.ChangeAsync(source, name, s => { Same(s); s.Stop(name); }, Settled(ServiceRunState.Stopped), cancellationToken).ConfigureAwait(false),
            ServiceActions.Restart => await access.ChangeAsync(source, name, s => { Same(s); stop = Restart(s, name, access.Time, cancellationToken); }, s => stop != RestartStop.Stopped || Settled(ServiceRunState.Running)(s), cancellationToken).ConfigureAwait(false),
            _ => await access.ChangeAsync(source, name, s => { Same(s); s.SetStartType(name, choice!.Value); }, _ => true, cancellationToken).ConfigureAwait(false),
        };
        if (result.Error is { } error)
        {
            ServiceAccess.Audit(byUser, action, name, source, (result.Partial ? "partly done: " : "failed: ") + error + types);
            return error;
        }

        var status = result.Value!;
        if (stop != RestartStop.Stopped)
        {
            ServiceAccess.Audit(byUser, action, name, source, "stopped only, not started again (" + stop + "): " + ServiceText.StateWord(status.State));
            return ServiceText.RestartNotStarted(name, source.Where, status.State, stop);
        }

        if (action != ServiceActions.StartType && status.State == ServiceRunState.Unknown)
        {
            ServiceAccess.Audit(byUser, action, name, source, "done, its state not read");
            return ServiceText.DoneUnread(action, name, source.Where);
        }

        if (starts && status.State == ServiceRunState.Stopped)
        {
            // The service gave up (G1): a start that ended stopped failed, whatever the wait called it.
            ServiceAccess.Audit(byUser, action, name, source, "the start failed: stopped, exit " + status.Win32ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return ServiceText.StartFailed(action, name, source.Where, status.Win32ExitCode, status.ServiceExitCode);
        }

        bool settled = action == ServiceActions.StartType
            || (Settled(action == ServiceActions.Stop ? ServiceRunState.Stopped : ServiceRunState.Running)(status) && status.State is ServiceRunState.Running or ServiceRunState.Stopped);
        if (!settled && cancellationToken.IsCancellationRequested)
        {
            // The change was made (ChangeAsync returns only after it); only its settling went unwatched (2026-10-23, T13).
            ServiceAccess.Audit(byUser, action, name, source, "made; stopped waiting at " + ServiceText.StateWord(status.State));
            return ComputerText.StoppedWaitingMade(ServiceText.StateWord(status.State));
        }

        string answer = action == ServiceActions.StartType ? ServiceText.StartTypeDone(name, source.Where, ServiceText.ChoiceWord(choice!.Value))
            : settled ? ServiceText.Done(action, name, source.Where, status.State)
                : ServiceText.StillPending(action, name, source.Where, status.State);
        ServiceAccess.Audit(byUser, action, name, source, ServiceText.StateWord(status.State) + types);
        return answer;
    }

    /// <summary>Whether a program line is a shared service host's (<c>svchost.exe -k …</c>), whose code is its DLL.</summary>
    public static bool IsShared(string imagePath) => imagePath.Contains("svchost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The service's stopped dependencies, which Windows starts before it (M3), followed through theirs; groups (<c>+</c>) left to Windows.
    /// Null past <see cref="MaxDependencies"/>.
    /// </summary>
    private static List<ServiceConfiguration>? StoppedDependencies(IServiceSession session, ServiceConfiguration config)
    {
        var found = new List<ServiceConfiguration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.Name };
        var next = new Queue<string>(config.Dependencies.Where(d => !d.StartsWith('+')));
        while (next.Count > 0)
        {
            string dependency = next.Dequeue();
            if (!seen.Add(dependency))
            {
                continue;
            }

            if (seen.Count > MaxDependencies + 1)
            {
                return null;
            }

            ServiceDetail detail;
            try
            {
                detail = session.Detail(dependency, dependents: false);
            }
            catch (ServiceException)
            {
                // A dependency that is missing or the account may not read is left to Windows (2026-10-23, the branch review of DP7): its
                // error failed the whole start and was worded against the service asked for.
                continue;
            }

            if (detail.Status.State != ServiceRunState.Stopped)
            {
                continue;
            }

            found.Add(detail.Config);
            foreach (string further in detail.Config.Dependencies.Where(d => !d.StartsWith('+')))
            {
                next.Enqueue(further);
            }
        }

        return found;
    }

    /// <summary>The wait's end: the wanted state, or a stop when a start failed (the service gave up).</summary>
    private static Func<ServiceStatus, bool> Settled(ServiceRunState wanted) => status =>
        status.State == wanted || (wanted == ServiceRunState.Running && status.State == ServiceRunState.Stopped && status.CheckPoint == 0 && status.Win32ExitCode is not 0);

    /// <summary>
    /// A restart inside one session: the state read here (B13: the read before the question could be stale by the yes), the stop when it is not
    /// stopped, its stopped state waited for by its wait hint (T14), then the start. The start is not tried when the stop did not end, the wait
    /// was stopped or the state could not be read — said apart. A start that throws after a stop is made in part (G2): the service is now down.
    /// </summary>
    private static RestartStop Restart(IServiceSession session, string name, TimeProvider time, CancellationToken cancellationToken)
    {
        var status = session.Status(name);
        if (status.State != ServiceRunState.Stopped)
        {
            var stopping = session.Stop(name);
            var (_, end) = ServiceAccess.WaitFor(session, name, stopping, s => s.State == ServiceRunState.Stopped, time, cancellationToken);
            if (end != ServiceWaitEnd.Settled)
            {
                return end switch
                {
                    ServiceWaitEnd.Cancelled => RestartStop.Cancelled,
                    ServiceWaitEnd.Unreadable => RestartStop.Unreadable,
                    _ => RestartStop.TimedOut,
                };
            }

            try
            {
                session.Start(name);
            }
            catch (ServiceException ex)
            {
                throw new ServiceException(ex.Code, ex.Message) { Applied = ServiceText.StoppedApplied };
            }

            return RestartStop.Stopped;
        }

        session.Start(name);
        return RestartStop.Stopped;
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
