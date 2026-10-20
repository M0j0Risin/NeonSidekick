using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Computers;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Services;

/// <summary>
/// What one services call came to: its value, or the sentence that says why not (never both). <see cref="Partial"/>: the change took effect in
/// part before its failure (2026-10-20), so the audit says so.
/// </summary>
public readonly record struct ServiceResult<T>(T? Value, string? Error, bool Partial = false)
{
    public static ServiceResult<T> Ok(T value) => new(value, null);

    public static ServiceResult<T> Failed(string error) => new(default, error);
}

/// <summary>Where one call goes: the computer, the target the session opens, and whether the model reads it redacted.</summary>
public sealed record ServiceSource(Computer<ServiceComputerConfig> Computer, ServiceTarget Target, bool Redacted)
{
    public bool Remote => Target.Remote;

    /// <summary>The computer as answers name it.</summary>
    public string Where => Computer.IsLocal ? ComputerText.ThisPc : Computer.Name;

    public ServiceErrorContext Context(string? service, bool write = false) => new(Computer.IsLocal ? ComputerText.ThisPc : Computer.Name, service, Remote, Computer.Account, write);
}

/// <summary>
/// The services' one way in (2026-10-15, the admin-tools plan), the tools' and <c>/services</c>' alike: the computer a call means among the
/// ticked ones (<c>Service computers offered</c>), its visible list, the sign-in (a <c>runas</c> password resolved per call), and the work on a
/// pool thread — a read under a 30 s (this PC) or 60 s (remote) timeout and abandoned when it runs out, a change waited out (its status polled,
/// bounded by the service's wait hint), never abandoned mid-call. <see cref="WriteRefusal"/> holds the one order of checks every change goes
/// through, the model's and the pane's alike (the pane skips only <c>Service writes</c>). Every change is audited.
/// </summary>
public sealed class ServiceAccess
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The longest service name the Service Control Manager takes (its key in the registry).</summary>
    public const int MaxNameLength = 256;

    /// <summary>The status a change answers with when none could be read after it (2026-10-20): its state <see cref="ServiceRunState.Unknown"/>.</summary>
    public static readonly ServiceStatus Unread = new(ServiceRunState.Unknown, 0, 0, 0, 0, 0, 0);

    /// <summary>The longest a change's status is polled for, whatever the wait hint says.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private readonly Func<ComputerCatalog<ServiceComputerConfig>> _loaded;
    private readonly Func<IReadOnlyList<string>?> _offered;

    public ServiceAccess(IServiceSystem? system, Func<ComputerCatalog<ServiceComputerConfig>> loaded, Func<IReadOnlyList<string>?> offered, TimeProvider time)
    {
        System = system;
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
        _offered = offered ?? throw new ArgumentNullException(nameof(offered));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IServiceSystem? System { get; }

    public TimeProvider Time { get; }

    public static ComputerFamilyInfo Family => ServiceConfigFile.Info;

    /// <summary>Every computer the files hold, ticked or not.</summary>
    public ComputerCatalog<ServiceComputerConfig> Loaded() => _loaded();

    /// <summary>The ticked computers.</summary>
    public ComputerCatalog<ServiceComputerConfig> Catalog() => _loaded().Offered(_offered());

    /// <summary>The computer <paramref name="computer"/> names among the ticked ones (or the default), its <c>runas</c> password resolved now.</summary>
    public (ServiceSource? Source, string? Error) Computer(string? computer, bool forceRedacted)
    {
        var (found, error) = Catalog().Find(computer);
        if (found is null)
        {
            return (null, error);
        }

        bool redacted = forceRedacted || !found.Config.Full;
        if (found.IsLocal)
        {
            return (new ServiceSource(found, ServiceTarget.Local, redacted), null);
        }

        ServiceLogin? login = null;
        if (found.Config.IsRunAs)
        {
            var secret = ServiceConfigFile.Family.ResolvePassword(found);
            if (secret.Error is { } missing)
            {
                return (null, ComputerText.CannotSignIn(found.Name, missing));
            }

            login = new ServiceLogin(found.Config.User!.Trim(), secret.Value!);
        }

        return (new ServiceSource(found, new ServiceTarget(found.Config.Host!.Trim(), login), redacted), null);
    }

    /// <summary>
    /// Why <paramref name="name"/> is no service's name, or null (2026-10-20, a review): empty, past <see cref="MaxNameLength"/>, or holding a
    /// control character, a slash or a backslash. A NUL ended the name OpenServiceW read, so the checks below judged one name and Windows opened
    /// another; every name a call carries is checked here first. Pure.
    /// </summary>
    public static string? NameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length == 0 || name.Length > MaxNameLength || name.Any(c => char.IsControl(c) || c is '/' or '\\') ? ServiceText.BadName(name) : null;
    }

    /// <summary>Whether <paramref name="name"/> is in the computer's visible list (absent, all).</summary>
    public static bool Visible(Computer<ServiceComputerConfig> computer, string name) =>
        computer.Config.Services is not { } patterns || NameGlob.Any(patterns, name);

    /// <summary>A read: on a pool thread under the timeout, abandoned when it runs out; the failure as its sentence.</summary>
    public async Task<ServiceResult<T>> ReadAsync<T>(ServiceSource source, string? service, Func<IServiceSession, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (System is not { } system)
        {
            return ServiceResult<T>.Failed(ServiceText.NoneHere);
        }

        using var timeout = new CancellationTokenSource(source.Remote ? RemoteTimeout : LocalTimeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await Task.Run(() => system.Run(source.Target, work), CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
            DiagnosticLog.Debug(ServiceText.Category, $"{Describe(source)} read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return ServiceResult<T>.Ok(value);
        }
        catch (ServiceException ex)
        {
            DiagnosticLog.Debug(ServiceText.Category, $"{Describe(source)} failed: {ex.Message}");
            return ServiceResult<T>.Failed(ServiceErrors.Sentence(ex.Code, source.Context(service), ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ServiceResult<T>.Failed(ServiceText.TimedOut(source.Where));
        }
    }

    /// <summary>
    /// The one order of checks a change goes through after the switch and the name's own (<see cref="NameProblem"/>, 2026-10-20) (2026-10-15, the plan's): <c>access</c> → the visible list → <c>control</c>
    /// → a driver → the app's protected list → the file's → <c>controlActions</c>. The dependents (stop and restart) and the approval follow
    /// in the caller, which has the session. <paramref name="writesOn"/> false refuses first (the model's calls; the pane passes true). Null
    /// when the change may go on to them. Pure.
    /// </summary>
    public static string? WriteRefusal(Computer<ServiceComputerConfig> computer, string name, string action, ServiceConfiguration? config, bool writesOn)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(name);
        string where = computer.IsLocal ? ComputerText.ThisPc : computer.Name;
        if (!writesOn)
        {
            return ServiceText.WritesOff;
        }

        if (NameProblem(name) is { } bad)
        {
            return bad;
        }

        if (!computer.Config.ReadWrite)
        {
            return ComputerText.ReadOnlyComputer(where, Family);
        }

        if (!Visible(computer, name))
        {
            return ComputerText.NotVisible(name, where, Family);
        }

        if (!computer.Config.InControl(name, Family.Separator))
        {
            return ComputerText.NotInControl(name, where, Family);
        }

        if (config is { IsDriver: true })
        {
            return ServiceText.Driver(name);
        }

        if (ServiceActions.TakesDown(action))
        {
            switch (ServiceGuard.Protection(name, computer.Config))
            {
                case ServiceProtection.App:
                    return ServiceText.ProtectedByApp(name);
                case ServiceProtection.File:
                    return ServiceText.ProtectedByFile(name);
            }
        }

        if (!computer.Config.AllowsAction(action))
        {
            return ComputerText.ActionNotAllowed(computer.Config.AllowedActions(Family), Family);
        }

        return null;
    }

    /// <summary>
    /// A change: <paramref name="act"/> on a pool thread inside one session, then the status polled until <paramref name="settled"/> or the
    /// bound (the wait hint, at least 1 s, at most <see cref="MaxWait"/>). The native calls are waited out, never abandoned; a cancel ends only
    /// the waiting (<see cref="ComputerText.StoppedWaiting"/>), the state at that moment logged. The final status, or the failure's sentence.
    /// Once <paramref name="act"/> is done the change counts as made (2026-10-20, a review: a status read failing after it was a "failed"): a read
    /// that fails then ends the wait with the last status read, or <see cref="Unread"/>; a failure that says what took effect
    /// (<see cref="ServiceException.Applied"/>) is <see cref="ServiceResult{T}.Partial"/>.
    /// </summary>
    public async Task<ServiceResult<ServiceStatus>> ChangeAsync(ServiceSource source, string name, Action<IServiceSession> act, Func<ServiceStatus, bool> settled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(settled);
        if (System is not { } system)
        {
            return ServiceResult<ServiceStatus>.Failed(ServiceText.NoneHere);
        }

        var change = Task.Run(() => system.Run(source.Target, session =>
        {
            act(session);
            if (TryStatus(session, name, source) is not { } status)
            {
                return Unread;
            }

            var deadline = Time.GetUtcNow() + Bound(status.WaitHintMs);
            // The polls are counted too (at most MaxWait / 250 ms), so a clock that does not move (a test's) still ends the wait.
            int polls = 0;
            while (!settled(status) && Time.GetUtcNow() < deadline && !cancellationToken.IsCancellationRequested && polls++ < MaxWait.TotalMilliseconds / 250)
            {
                Thread.Sleep(Math.Clamp((int)status.WaitHintMs / 10, 250, 2000));
                if (TryStatus(session, name, source) is not { } next)
                {
                    break;
                }

                status = next;
                deadline = Max(deadline, Time.GetUtcNow() + Bound(status.WaitHintMs) / 4);
                deadline = Min(deadline, Time.GetUtcNow() + MaxWait);
            }

            return status;
        }), CancellationToken.None);
        try
        {
            return ServiceResult<ServiceStatus>.Ok(await change.ConfigureAwait(false));
        }
        catch (ServiceException ex)
        {
            DiagnosticLog.Debug(ServiceText.Category, $"{Describe(source)} change failed: {ex.Message}");
            string sentence = ServiceErrors.Sentence(ex.Code, source.Context(name, write: true), ex.Message);
            return ex.Applied is { } applied
                ? new ServiceResult<ServiceStatus>(null, ServiceText.Partly(name, source.Where, applied, sentence), Partial: true)
                : ServiceResult<ServiceStatus>.Failed(sentence);
        }
    }

    /// <summary>A status read after the change was made: null when it fails (logged), never a failure of the change.</summary>
    private static ServiceStatus? TryStatus(IServiceSession session, string name, ServiceSource source)
    {
        try
        {
            return session.Status(name);
        }
        catch (ServiceException ex)
        {
            DiagnosticLog.Debug(ServiceText.Category, $"{Describe(source)} status after the change not read: {ex.Message}");
            return null;
        }
    }

    private static TimeSpan Bound(uint waitHintMs) => TimeSpan.FromMilliseconds(Math.Clamp(waitHintMs, 1000, (uint)MaxWait.TotalMilliseconds));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>The audit line of one change (Docker's shape): who, what, where, how it went.</summary>
    public static void Audit(bool byUser, string action, string name, ServiceSource source, string outcome)
    {
        ArgumentNullException.ThrowIfNull(source);
        DiagnosticLog.Info(ServiceText.Category, ComputerText.AuditLine(byUser ? ComputerText.ByYou : ComputerText.ByModel, ServiceText.AuditAct(action, name), source.Where, outcome));
    }

    /// <summary>
    /// A computer tried as it stands (the wizard's Test and <c>--services-check</c>): the services listed under its account, nothing changed;
    /// whether a write could work is said apart (<paramref name="canChange"/> under <c>readwrite</c>). Its sentence either way.
    /// </summary>
    public static async Task<(bool Ok, string Text)> TestAsync(IServiceSystem? system, Computer<ServiceComputerConfig> computer, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var access = new ServiceAccess(system, () => new ComputerCatalog<ServiceComputerConfig>([computer], [], Family), () => [computer.Name], time);
        var (source, error) = access.Computer(computer.Name, forceRedacted: true);
        if (source is null)
        {
            return (false, error!);
        }

        var result = await access.ReadAsync(source, null, session => session.List(drivers: false), cancellationToken).ConfigureAwait(false);
        return result.Error is { } failed
            ? (false, failed)
            : (true, ServiceText.TestOk(computer.Name, result.Value!.Count, result.Value.Count(s => s.State == ServiceRunState.Running)));
    }

    private static string Describe(ServiceSource source) => source.Remote ? source.Computer.Name + " (" + source.Target.Host + ")" : ComputerConfig.LocalName;
}
