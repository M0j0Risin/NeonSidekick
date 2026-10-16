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

    /// <summary>Whether a <c>control</c> pattern on the computer reaches into <c>\Microsoft\</c>.</summary>
    public static bool ControlsMicrosoft(TaskComputerConfig config) => config.Control is { } patterns && patterns.Any(p => IsMicrosoft(p.Trim()));

    public async Task<TaskResult<T>> ReadAsync<T>(TaskSource source, string? task, Func<ITaskSession, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (System is not { } system)
        {
            return TaskResult<T>.Failed(TaskText.NoneHere);
        }

        using var timeout = new CancellationTokenSource(source.Remote ? RemoteTimeout : LocalTimeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await Task.Run(() => system.Run(source.Target, work), CancellationToken.None).WaitAsync(linked.Token).ConfigureAwait(false);
            DiagnosticLog.Debug(TaskText.Category, $"{Describe(source)} read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return TaskResult<T>.Ok(value);
        }
        catch (TaskException ex)
        {
            DiagnosticLog.Debug(TaskText.Category, $"{Describe(source)} failed: {ex.Message}");
            return TaskResult<T>.Failed(TaskErrors.Sentence(ex.Code, source.Context(task), ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TaskResult<T>.Failed(TaskText.TimedOut(source.Where));
        }
    }

    /// <summary>A change on a pool thread, waited out; the entry after it, or the failure's sentence.</summary>
    public async Task<TaskResult<TaskEntry>> ChangeAsync(TaskSource source, string path, Action<ITaskSession> act)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(act);
        if (System is not { } system)
        {
            return TaskResult<TaskEntry>.Failed(TaskText.NoneHere);
        }

        try
        {
            var entry = await Task.Run(() => system.Run(source.Target, session =>
            {
                act(session);
                return session.Detail(path).Entry;
            }), CancellationToken.None).ConfigureAwait(false);
            return TaskResult<TaskEntry>.Ok(entry);
        }
        catch (TaskException ex)
        {
            return TaskResult<TaskEntry>.Failed(TaskErrors.Sentence(ex.Code, source.Context(path, write: true), ex.Message));
        }
    }

    /// <summary>
    /// The order of checks before the approval (2026-10-15, the plan's): the switch (<paramref name="writesOn"/>) → <c>access</c> → <c>folders</c>
    /// → <c>control</c> → <c>\Microsoft\</c> → <c>controlActions</c> → privileged (run and end, from <paramref name="definition"/> when read).
    /// Null when the change may go on to the approval. Pure.
    /// </summary>
    public static string? WriteRefusal(Computer<TaskComputerConfig> computer, string path, string action, TaskDefinition? definition, bool writesOn)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(path);
        string where = computer.IsLocal ? ComputerText.ThisPc : computer.Name;
        if (!writesOn)
        {
            return TaskText.WritesOff;
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

        if (IsMicrosoft(path) && !ControlsMicrosoft(computer.Config))
        {
            return TaskText.MicrosoftTask(path);
        }

        if (!computer.Config.AllowsAction(action))
        {
            return ComputerText.ActionNotAllowed(computer.Config.AllowedActions(Family), Family);
        }

        if (TaskActions.Runs(action) && definition is not null && TaskPrincipal.IsPrivileged(definition.Principal) && !computer.Config.AllowsPrivileged)
        {
            return TaskText.Privileged(path, TaskRender.PrincipalWords(definition.Principal));
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
