namespace NeonSidekick.Services;

/// <summary>A service's run state, as the Service Control Manager numbers it.</summary>
public enum ServiceRunState
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>A start type as the Service Control Manager numbers it; delayed is <see cref="Automatic"/> with a flag.</summary>
public enum ServiceStartType
{
    Boot = 0,
    System = 1,
    Automatic = 2,
    Manual = 3,
    Disabled = 4,
}

/// <summary>A start type a change asks for: the four the services snap-in offers.</summary>
public enum ServiceStartChoice
{
    Automatic,
    Delayed,
    Manual,
    Disabled,
}

/// <summary>One service as the list shows it.</summary>
public sealed record ServiceEntry(string Name, string DisplayName, ServiceRunState State, int ProcessId, uint ServiceType, uint Win32ExitCode, uint ServiceExitCode)
{
    /// <summary>A kernel or file-system driver, never controlled.</summary>
    public bool IsDriver => ServiceKinds.IsDriver(ServiceType);
}

/// <summary>A service's live status.</summary>
public sealed record ServiceStatus(ServiceRunState State, int ProcessId, uint Win32ExitCode, uint ServiceExitCode, uint CheckPoint, uint WaitHintMs, uint ControlsAccepted)
{
    /// <summary>Whether the service takes a stop now (<c>SERVICE_ACCEPT_STOP</c>).</summary>
    public bool AcceptsStop => (ControlsAccepted & 0x1) != 0;
}

/// <summary>One recovery action: what the SCM does after a failure, and after how long.</summary>
public sealed record ServiceFailureAction(int Type, uint DelayMs);

/// <summary>A service's recovery settings.</summary>
public sealed record ServiceFailureActions(uint ResetPeriodSeconds, string? Command, string? RebootMessage, IReadOnlyList<ServiceFailureAction> Actions);

/// <summary>A service's configuration, as <c>sc qc</c> and <c>sc qfailure</c> show it.</summary>
public sealed record ServiceConfiguration(
    string Name,
    string DisplayName,
    string? Description,
    uint ServiceType,
    ServiceStartType StartType,
    bool DelayedAutoStart,
    string ImagePath,
    string Account,
    string? LoadOrderGroup,
    IReadOnlyList<string> Dependencies,
    ServiceFailureActions? Failure)
{
    public bool IsDriver => ServiceKinds.IsDriver(ServiceType);
}

/// <summary>
/// Everything <c>service_info</c> shows: the configuration, the status, and who depends on it. <see cref="DependentsKnown"/> false when the
/// account may read the service but not list its dependents (2026-10-15: RpcSs and its like refuse SERVICE_ENUMERATE_DEPENDENTS to a user who
/// is not elevated); a stop then leaves the running dependents to the Service Control Manager, which refuses it with 1051.
/// </summary>
public sealed record ServiceDetail(ServiceConfiguration Config, ServiceStatus Status, IReadOnlyList<ServiceEntry> Dependents, bool DependentsKnown = true);

/// <summary>What the native layer threw: the Win32 code and where.</summary>
public sealed class ServiceException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>
    /// What took effect before the failure, in words (2026-10-20, a review: a start type set to automatic whose delayed flag was then refused
    /// was answered and audited as a plain failure); null when nothing did.
    /// </summary>
    public string? Applied { get; init; }
}

/// <summary>Who a session signs in as on a remote computer: the account and its password (never logged).</summary>
public sealed record ServiceLogin(string User, string Password);

/// <summary>Where a session goes: this PC (no host), or a remote computer, as the user or as <see cref="Login"/>.</summary>
public sealed record ServiceTarget(string? Host, ServiceLogin? Login)
{
    public static readonly ServiceTarget Local = new(null, null);

    public bool Remote => Host is not null;
}

/// <summary>One open session on a computer's Service Control Manager: everything the tools and <c>/services</c> read or change.</summary>
public interface IServiceSession
{
    /// <summary>Every service (and with <paramref name="drivers"/> every driver) with its status.</summary>
    IReadOnlyList<ServiceEntry> List(bool drivers);

    /// <summary>One service's configuration, status and dependents; a missing one throws code 1060.</summary>
    ServiceDetail Detail(string name);

    /// <summary>One service's status now.</summary>
    ServiceStatus Status(string name);

    /// <summary>One service's start type and whether automatic is delayed (the list's <c>start</c> filter reads it per service).</summary>
    (ServiceStartType Start, bool Delayed) StartOf(string name);

    /// <summary>The services that depend on <paramref name="name"/>, running ones only with <paramref name="activeOnly"/>.</summary>
    IReadOnlyList<ServiceEntry> Dependents(string name, bool activeOnly);

    /// <summary>Asks the service to start; returns once the SCM took the request.</summary>
    void Start(string name);

    /// <summary>Asks the service to stop; returns the status the SCM answered with.</summary>
    ServiceStatus Stop(string name);

    /// <summary>Changes the start type.</summary>
    void SetStartType(string name, ServiceStartChoice choice);
}

/// <summary>
/// The Service Control Manager (2026-10-15, the admin-tools plan): <see cref="WindowsServiceSystem"/> over advapi32 in-process; tests pass
/// <c>FakeServiceSystem</c>; null off Windows, where every call says there are no services here. A session is opened, worked and closed inside
/// <see cref="Run{T}"/> on the caller's thread (the door runs it on a pool thread), a remote <c>runas</c> impersonated around the whole of it.
/// </summary>
public interface IServiceSystem
{
    T Run<T>(ServiceTarget target, Func<IServiceSession, T> work);
}

/// <summary>The service type bits that matter here.</summary>
public static class ServiceKinds
{
    public const uint KernelDriver = 0x1;
    public const uint FileSystemDriver = 0x2;
    public const uint Adapter = 0x4;
    public const uint RecognizerDriver = 0x8;
    public const uint Driver = KernelDriver | FileSystemDriver | RecognizerDriver;
    public const uint Win32OwnProcess = 0x10;
    public const uint Win32ShareProcess = 0x20;
    public const uint Win32 = Win32OwnProcess | Win32ShareProcess;
    public const uint UserService = 0x40;
    public const uint UserServiceInstance = 0x80;

    public static bool IsDriver(uint type) => (type & Driver) != 0 && (type & Win32) == 0;
}
