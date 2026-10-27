using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Computers;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Tasks;

/// <summary>What one tasks call came to: its value, or the sentence that says why not.</summary>
public readonly record struct TaskResult<T>(T? Value, string? Error)
{
    public static TaskResult<T> Ok(T value) => new(value, null);

    public static TaskResult<T> Failed(string error) => new(default, error);
}

/// <summary>Where one call goes: the computer, the target, and whether the model reads it redacted.</summary>
public sealed record TaskSource(Computer<TaskComputerConfig> Computer, TaskTarget Target, bool Redacted)
{
    public bool Remote => Target.Remote;

    public string Where => Computer.IsLocal ? ComputerText.ThisPc : Computer.Name;

    public TaskErrorContext Context(string? task, bool write = false) => new(Where, task, Remote, Computer.Account, write);
}

/// <summary>
/// The scheduled tasks' one way in (2026-10-15, the admin-tools plan, <c>ServiceAccess</c>'s twin): the ticked computers (<c>Task computers
/// offered</c>), the visible folders, the sign-in, and the work on a pool thread — a read under 30 s (this PC) or 60 s (remote), abandoned when
/// it runs out; a change waited out. <see cref="WriteRefusal"/> holds the one order of checks: the switch → <c>access</c> → <c>folders</c> →
/// <c>control</c> → <c>\Microsoft\</c> → <c>controlActions</c> → privileged, the approval after. Every change is audited.
/// </summary>
public sealed class TaskAccess
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Windows' own tasks' folder.</summary>
    public const string MicrosoftFolder = "\\Microsoft\\";

    private readonly Func<ComputerCatalog<TaskComputerConfig>> _loaded;
    private readonly Func<IReadOnlyList<string>?> _offered;

    public TaskAccess(ITaskSystem? system, Func<ComputerCatalog<TaskComputerConfig>> loaded, Func<IReadOnlyList<string>?> offered, TimeProvider time)
    {
        System = system;
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
        _offered = offered ?? throw new ArgumentNullException(nameof(offered));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public ITaskSystem? System { get; }

    public TimeProvider Time { get; }

    public static ComputerFamilyInfo Family => TaskConfigFile.Info;

    public ComputerCatalog<TaskComputerConfig> Loaded() => _loaded();

    public ComputerCatalog<TaskComputerConfig> Catalog() => _loaded().Offered(_offered());

    private readonly object _seenGate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The folders of a task list just read on <paramref name="source"/>'s computer, remembered for <c>/tasks folder:\</c>'s argument list
    /// (2026-10-27, the completion survey): each task's folder and the folders above it, the root left out; the last list read per computer
    /// replaces the one before. In memory only, under its own lock (completion reads it off the screen's thread). The callers: <c>/tasks</c>,
    /// its pane and <c>task_list</c>.
    /// </summary>
    public void Saw(TaskSource source, IEnumerable<TaskEntry> tasks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tasks);
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            for (string folder = task.Folder.TrimEnd('\\'); folder.Length > 0; folder = folder[..folder.LastIndexOf('\\')])
            {
                folders.Add(folder);
            }
        }

        lock (_seenGate)
        {
            _seen[SeenKey(source.Computer.Name)] = [.. folders];
        }
    }

    /// <summary>The folders <see cref="Saw"/> kept for <paramref name="computer"/> (null or <c>local</c>: this PC), as <c>\Ops\Sync</c>; none before a list is read.</summary>
    public IReadOnlyList<string> FoldersSeen(string? computer)
    {
        lock (_seenGate)
        {
            return _seen.TryGetValue(SeenKey(computer), out var folders) ? folders : [];
        }
    }

    private static string SeenKey(string? computer) => string.IsNullOrWhiteSpace(computer) ? ComputerConfig.LocalName : computer.Trim();

    public (TaskSource? Source, string? Error) Computer(string? computer, bool forceRedacted)
    {
        var (found, error) = Catalog().Find(computer);
        if (found is null)
        {
            return (null, error);
        }

        bool redacted = forceRedacted || !found.Config.Full;
        if (found.IsLocal)
        {
            return (new TaskSource(found, TaskTarget.Local, redacted), null);
        }

        TaskLogin? login = null;
        if (found.Config.IsRunAs)
        {
            var secret = TaskConfigFile.Family.ResolvePassword(found);
            if (secret.Error is { } missing)
            {
                return (null, ComputerText.CannotSignIn(found.Name, missing));
            }

            login = new TaskLogin(found.Config.User!.Trim(), secret.Value!);
        }

        return (new TaskSource(found, new TaskTarget(found.Config.Host!.Trim(), login), redacted), null);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is visible on <paramref name="computer"/>: absent <c>folders</c>, all; else a pattern matches the path
    /// (<c>\</c> the separator; a pattern ending in <c>\</c> means the tasks directly in that folder).
    /// </summary>
    public static bool Visible(Computer<TaskComputerConfig> computer, string path)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return computer.Config.Folders is not { } patterns || patterns.Any(p => Matches(p.Trim(), path));
    }

    /// <summary>A task-path pattern against a path: a trailing <c>\</c> means the folder's own tasks.</summary>
    public static bool Matches(string pattern, string path) =>
        NameGlob.Matches(pattern.EndsWith('\\') ? pattern + "*" : pattern, path, '\\');

    /// <summary>Whether <paramref name="path"/> is under <c>\Microsoft\</c>.</summary>
    public static bool IsMicrosoft(string path) => path.StartsWith(MicrosoftFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a <c>control</c> pattern that starts with <c>\Microsoft\</c> matches <paramref name="path"/> (2026-10-20, a review: any such
    /// pattern on the computer once let a broader one, <c>\*\*\*</c>, reach every Windows task).
    /// </summary>
    public static bool ControlsMicrosoft(TaskComputerConfig config, string path) =>
        config.Control is { } patterns && patterns.Any(p => IsMicrosoft(p.Trim()) && Matches(p.Trim(), path));

    /// <summary>The longest task path a call may carry.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>
    /// Why <paramref name="path"/> is no task's path, or null (2026-10-20, a review: <c>\Backup\../Secret/Task</c> passed a <c>\Backup\*</c>
    /// folder pattern, whose star does not cross <c>\</c> but does cross <c>/</c>): it must start with <c>\</c>, hold no control character and no
    /// <c>/</c>, no empty, <c>.</c> or <c>..</c> part, and be at most <see cref="MaxPathLength"/> characters. Pure.
    /// </summary>
    public static string? PathProblem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length is < 2 or > MaxPathLength || path[0] != '\\' || path.Any(c => char.IsControl(c) || c == '/'))
        {
            return TaskText.BadPath(path);
        }

        return path[1..].Split('\\').Any(part => part.Length == 0 || part.Trim() is "." or "..") ? TaskText.BadPath(path) : null;
    }

    /// <summary>
    /// A list's folder as the Task Scheduler takes it, or the refusal (2026-10-23, the admin review's R3: only a leading <c>\</c> was checked, so
    /// <c>Backup</c> was refused and <c>\a/b</c>, <c>\..</c> or a control character reached GetFolder as a COM error): blank or <c>\</c> is the
    /// root; a missing lead is added, a trailing <c>\</c> dropped, and the rest judged by <see cref="PathProblem"/>.
    /// </summary>
    public static (string? Folder, string? Error) Folder(string given)
    {
        ArgumentNullException.ThrowIfNull(given);
        string folder = given.Trim();
        if (folder.Length == 0 || folder == "\\")
        {
            return ("\\", null);
        }

        folder = ("\\" + folder.TrimStart('\\')).TrimEnd('\\');
        return folder.Length > 1 && PathProblem(folder) is null ? (folder, null) : (null, TaskText.BadFolder(given));
    }

    public async Task<TaskResult<T>> ReadAsync<T>(TaskSource source, string? task, Func<ITaskSession, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (System is not { } system)
        {
            return TaskResult<T>.Failed(TaskText.NoneHere);
        }

        // Through the shared sign-in guard (2026-10-23, the admin review's F2, B2).
        var started = Stopwatch.GetTimestamp();
        var run = await SignInGuard.RunAsync(Guarded(source), _ => system.Run(source.Target, work), source.Remote ? RemoteTimeout : LocalTimeout, Time, cancellationToken).ConfigureAwait(false);
        if (Stopped(run.Stop, source) is { } stop)
        {
            return TaskResult<T>.Failed(stop);
        }

        if (run.Failure is TaskException ex)
        {
            DiagnosticLog.Debug(TaskText.Category, $"{Describe(source)} failed: {ex.Message}");
            return TaskResult<T>.Failed(TaskErrors.Sentence(ex.Code, source.Context(task), ex.Message));
        }

        if (run.Failure is { } other)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(other).Throw();
        }

        DiagnosticLog.Debug(TaskText.Category, $"{Describe(source)} read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        return TaskResult<T>.Ok(run.Value!);
    }

    /// <summary>The guard's facts for <paramref name="source"/> (the sign-in held with the services' and event logs' of one host).</summary>
    private static SignInCall Guarded(TaskSource source) =>
        SignInGuard.ForHost("tasks", source.Target.Host, source.Target.Login?.User, source.Target.Login?.Password, failure => failure is TaskException { Code: var code } && SignInGuard.IsWrongPassword(code));

    /// <summary>A guarded call's stop as its sentence, or null when the work ran.</summary>
    private static string? Stopped(SignInStop stop, TaskSource source) => stop switch
    {
        SignInStop.Held => ComputerText.Held(source.Where, source.Computer.Account),
        SignInStop.StillWaiting => ComputerText.StillWaiting(source.Where),
        SignInStop.TimedOut => TaskText.TimedOut(source.Where),
        _ => null,
    };

    /// <summary>
    /// A change on a pool thread, waited out; the entry after it, or the failure's sentence. Once <paramref name="act"/> is done the change counts
    /// as made (2026-10-20, a review: a read back that failed, the instances refused, answered "failed" and the model ran the task again): the
    /// entry read back alone, and null when that fails.
    /// </summary>
    public async Task<TaskResult<TaskEntry?>> ChangeAsync(TaskSource source, string path, Action<ITaskSession> act, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(act);
        if (System is not { } system)
        {
            return TaskResult<TaskEntry?>.Failed(TaskText.NoneHere);
        }

        // On the guard (2026-10-23, the admin review's B2): the connect and the COM open answer Esc and the timeout; the change begins only
        // after them and is then waited out, so a cancel never leaves it half made.
        var run = await SignInGuard.RunAsync(Guarded(source), turn => system.Run(source.Target, session =>
        {
            turn.Begin();
            act(session);
            try
            {
                return session.Entry(path);
            }
            catch (TaskException ex)
            {
                DiagnosticLog.Debug(TaskText.Category, $"{Describe(source)} {path} not read after the change: {ex.Message}");
                return null;
            }
        }), source.Remote ? RemoteTimeout : LocalTimeout, Time, cancellationToken).ConfigureAwait(false);
        if (Stopped(run.Stop, source) is { } stop)
        {
            return TaskResult<TaskEntry?>.Failed(run.Stop == SignInStop.TimedOut ? TaskText.NotChangedTimedOut(path, source.Where) : stop);
        }

        if (run.Failure is TaskException ex)
        {
            return TaskResult<TaskEntry?>.Failed(TaskErrors.Sentence(ex.Code, source.Context(path, write: true), ex.Message));
        }

        if (run.Failure is { } other)
        {
            global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(other).Throw();
        }

        return TaskResult<TaskEntry?>.Ok(run.Value);
    }

    /// <summary>
    /// The order of checks before the approval (2026-10-15, the plan's): the switch (<paramref name="writesOn"/>) → the path's own
    /// (<see cref="PathProblem"/>, 2026-10-20) → <c>access</c> → <c>folders</c> → <c>control</c> → <c>\Microsoft\</c> (a pattern starting there
    /// that matches) → <c>controlActions</c> → privileged (run, end and enable, from <paramref name="definition"/> when read, for a session
    /// connected as <paramref name="self"/>). Null when the change may go on to the approval. Pure.
    /// </summary>
    public static string? WriteRefusal(Computer<TaskComputerConfig> computer, string path, string action, TaskDefinition? definition, bool writesOn, IReadOnlyCollection<string>? self = null, bool principalUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(path);
        string where = computer.IsLocal ? ComputerText.ThisPc : computer.Name;
        if (!writesOn)
        {
            return TaskText.WritesOff;
        }

        if (PathProblem(path) is { } bad)
        {
            return bad;
        }

        if (!computer.Config.ReadWrite)
        {
            return ComputerText.ReadOnlyComputer(where, Family);
        }

        if (!Visible(computer, path))
        {
            return ComputerText.NotVisible(path, where, Family);
        }

        if (!(computer.Config.Control is { } control && control.Any(p => Matches(p.Trim(), path))))
        {
            return ComputerText.NotInControl(path, where, Family);
        }

        if (IsMicrosoft(path) && !ControlsMicrosoft(computer.Config, path))
        {
            return TaskText.MicrosoftTask(path);
        }

        if (!computer.Config.AllowsAction(action))
        {
            return ComputerText.ActionNotAllowed(computer.Config.AllowedActions(Family), Family);
        }

        if (TaskActions.NeedsPrivilege(action) && definition is not null && TaskPrincipal.IsPrivileged(definition.Principal, self) && !computer.Config.AllowsPrivileged)
        {
            return TaskText.Privileged(path, TaskRender.PrincipalWords(definition.Principal));
        }

        if (TaskActions.NeedsPrivilege(action) && definition is null && principalUnknown && !computer.Config.AllowsPrivileged)
        {
            // An unreadable definition counts as privileged, said as not known (2026-10-23, the admin review's T12: a made-up SYSTEM principal
            // was stated as fact).
            return TaskText.PrivilegedUnknown(path);
        }

        return null;
    }

    public static void Audit(bool byUser, string action, string path, TaskSource source, string outcome)
    {
        ArgumentNullException.ThrowIfNull(source);
        DiagnosticLog.Info(TaskText.Category, ComputerText.AuditLine(byUser ? ComputerText.ByYou : ComputerText.ByModel, TaskText.AuditAct(action, path), source.Where, outcome));
    }

    /// <summary>A computer tried as it stands (the wizard's Test and <c>--tasks-check</c>): connected and the tasks listed, nothing changed.</summary>
    public static async Task<(bool Ok, string Text)> TestAsync(ITaskSystem? system, Computer<TaskComputerConfig> computer, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var access = new TaskAccess(system, () => new ComputerCatalog<TaskComputerConfig>([computer], [], Family), () => [computer.Name], time);
        var (source, error) = access.Computer(computer.Name, forceRedacted: true);
        if (source is null)
        {
            return (false, error!);
        }

        var result = await access.ReadAsync(source, null, session => (session.TargetServer, session.List("\\", recursive: true).Count(t => !IsMicrosoft(t.Path))), cancellationToken).ConfigureAwait(false);
        return result.Error is { } failed ? (false, failed) : (true, TaskText.TestOk(computer.Name, result.Value.TargetServer, result.Value.Item2));
    }

    private static string Describe(TaskSource source) => source.Remote ? source.Computer.Name + " (" + source.Target.Host + ")" : ComputerConfig.LocalName;
}
