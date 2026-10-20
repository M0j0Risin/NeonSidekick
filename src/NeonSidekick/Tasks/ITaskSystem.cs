namespace NeonSidekick.Tasks;

/// <summary>A task's state, as the Task Scheduler numbers it (<c>TASK_STATE</c>).</summary>
public enum TaskRunState
{
    Unknown = 0,
    Disabled = 1,
    Queued = 2,
    Ready = 3,
    Running = 4,
}

/// <summary>One registered task as the list shows it. Times are the computer's own local time (COM's DATE carries no zone).</summary>
public sealed record TaskEntry(string Path, string Name, TaskRunState State, bool Enabled, DateTime? LastRun, int LastResult, DateTime? NextRun, int MissedRuns)
{
    /// <summary>The folder the task sits in: <c>\</c> or <c>\Backup\</c>.</summary>
    public string Folder => Path.LastIndexOf('\\') is var i and > 0 ? Path[..(i + 1)] : "\\";
}

/// <summary>One running instance of a task.</summary>
public sealed record TaskInstance(string InstanceGuid, string CurrentAction, int EnginePid, TaskRunState State);

/// <summary>Everything <c>task_info</c> shows: the entry, its definition's XML, and its running instances.</summary>
public sealed record TaskDetail(TaskEntry Entry, string Xml, IReadOnlyList<TaskInstance> Running);

/// <summary>What the native layer threw: the HRESULT and where.</summary>
public sealed class TaskException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Who a session signs in as on a remote computer: the account and its password (never logged), straight into <c>Connect</c>.</summary>
public sealed record TaskLogin(string User, string Password);

/// <summary>Where a session goes: this PC (no host), or a remote computer, as the user or as <see cref="Login"/>.</summary>
public sealed record TaskTarget(string? Host, TaskLogin? Login)
{
    public static readonly TaskTarget Local = new(null, null);

    public bool Remote => Host is not null;
}

/// <summary>One connected session on a computer's Task Scheduler.</summary>
public interface ITaskSession
{
    /// <summary>The computer the service is connected to.</summary>
    string TargetServer { get; }

    /// <summary>The highest Task Scheduler version it speaks (high word major, low word minor).</summary>
    int HighestVersion { get; }

    /// <summary>
    /// The account the session connected as, in every form a task's <c>UserId</c> may name it (2026-10-20, a review): domain\name, the bare
    /// name for this PC's own local account, and its SID when it could be looked up. A task that runs as anyone else is privileged (<see cref="TaskPrincipal"/>).
    /// </summary>
    IReadOnlyList<string> Self { get; }

    /// <summary>One task's entry alone (a change's read back, 2026-10-20: <see cref="Detail"/> also lists its instances, which may be refused).</summary>
    TaskEntry Entry(string path);

    /// <summary>The tasks in <paramref name="folder"/>, and with <paramref name="recursive"/> every folder under it; hidden ones too.</summary>
    IReadOnlyList<TaskEntry> List(string folder, bool recursive);

    /// <summary>One task: its entry, definition XML and running instances; a missing one throws 0x80070002.</summary>
    TaskDetail Detail(string path);

    /// <summary>Runs the task now, no arguments.</summary>
    void Run(string path);

    /// <summary>Ends every running instance.</summary>
    void End(string path);

    /// <summary>Enables or disables the task.</summary>
    void SetEnabled(string path, bool enabled);
}

/// <summary>
/// The Task Scheduler (2026-10-15, the admin-tools plan): <see cref="WindowsTaskSystem"/> over its COM API in-process; tests pass
/// <c>FakeTaskSystem</c>; null off Windows, where every call says there are no scheduled tasks here. A session is created, connected, worked and
/// released inside <see cref="Run{T}"/>.
/// </summary>
public interface ITaskSystem
{
    T Run<T>(TaskTarget target, Func<ITaskSession, T> work);
}
