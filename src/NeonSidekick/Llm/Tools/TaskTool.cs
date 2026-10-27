using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the three task tools share (2026-10-15, the admin-tools plan, <see cref="ServiceTool"/>'s twin): the <see cref="TaskAccess"/> door, the
/// settings, the arguments' readers and the redacted twin. The reads are offered headless too; <c>task_control</c> only with <c>Task writes</c>, a
/// pane and a ticked <c>readwrite</c> computer whose <c>controlActions</c> is not empty — never headless, never to a bot.
/// </summary>
public abstract class TaskTool : AIFunction, IRedactedTwin
{
    public const string PathArgument = "path";
    public const string ComputerArgument = "computer";

    protected TaskTool(TaskAccess access, Func<AppSettingsData> effective, bool forceRedacted)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        ForceRedacted = forceRedacted;
    }

    protected TaskAccess Access { get; }

    protected Func<AppSettingsData> Effective { get; }

    public bool ForceRedacted { get; }

    public abstract AIFunction AsRedacted();

    // The shared readers (2026-10-23, the admin review's R18, E7, K6: AdminArguments).
    protected static string? Optional(AIFunctionArguments arguments, string name) => AdminArguments.Optional(arguments, name);

    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback) => AdminArguments.Flag(arguments, name, fallback);

    protected static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback) => AdminArguments.Number(arguments, name, min, max, fallback);

    /// <summary>A task path as given: a leading <c>\</c> added when left out.</summary>
    protected static string TaskPath(string path) => path.StartsWith('\\') ? path : "\\" + path;
}

/// <summary><c>task_list(computer?, folder?, recursive?, state?, filter?, include_microsoft?, limit?, offset?)</c> (2026-10-15).</summary>
public sealed class TaskListTool(TaskAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : TaskTool(access, effective, forceRedacted)
{
    public const string ToolName = "task_list";
    public const string FolderArgument = "folder";
    public const string RecursiveArgument = "recursive";
    public const string StateArgument = "state";
    public const string FilterArgument = "filter";
    public const string MicrosoftArgument = "include_microsoft";
    public const string LimitArgument = "limit";
    public const string OffsetArgument = "offset";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "computer": { "type": "string", "description": "Which computer; leave out for this PC (or the only one offered)." },
            "folder": { "type": "string", "description": "The folder to list, e.g. \"\\\" (default) or \"\\Backup\"." },
            "recursive": { "type": "boolean", "description": "Include the folders under it (default true)." },
            "state": { "type": "string", "description": "ready, running, disabled or queued." },
            "filter": { "type": "string", "description": "Only tasks whose path contains this text." },
            "include_microsoft": { "type": "boolean", "description": "Include Windows' own tasks under \\Microsoft\\ (default false; a folder under it lists them anyway)." },
            "limit": { "type": "integer", "description": "The most to list, 1 to 500 (default 50)." },
            "offset": { "type": "integer", "description": "Skip this many matches (the answer's next call says)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists the scheduled tasks on a computer with their states, last run, last result and next run.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new TaskListTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        // The folder judged as a path (2026-10-23, the admin review's R3).
        var (folder, folderError) = TaskAccess.Folder(Optional(arguments, FolderArgument) ?? "\\");
        if (folder is null)
        {
            return folderError;
        }

        string? state = Optional(arguments, StateArgument)?.ToLowerInvariant();
        if (state is not null && !TaskFilter.States.Contains(state))
        {
            return TaskText.BadState(state);
        }

        var (recursive, recursiveError) = Flag(arguments, RecursiveArgument, fallback: true);
        var (microsoft, microsoftError) = Flag(arguments, MicrosoftArgument, fallback: false);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, MaxLimit, DefaultLimit);
        var (offset, offsetError) = Number(arguments, OffsetArgument, 0, 100_000, 0);
        if ((recursiveError ?? microsoftError ?? limitError ?? offsetError) is { } argumentError)
        {
            return argumentError;
        }

        string? computer = Optional(arguments, ComputerArgument);
        var (source, refused) = Access.Computer(computer, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        var filter = new TaskFilter(folder, recursive, state, Optional(arguments, FilterArgument), microsoft);
        var result = await Access.ReadAsync(source, folder, session => Matches(session, source, filter), cancellationToken).ConfigureAwait(false);
        if (result.Error is null)
        {
            Access.Saw(source, result.Value.Matched);
        }

        return result.Error ?? TaskRender.List(source.Where, result.Value.Matched, result.Value.Total, offset, limit, filter, computer);
    }

    /// <summary>The visible tasks under the folder that pass the filter, by path; <c>\Microsoft\</c> not walked unless shown (B4).</summary>
    public static (IReadOnlyList<TaskEntry> Matched, int Total) Matches(ITaskSession session, TaskSource source, TaskFilter filter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        string folder = filter.Folder.TrimEnd('\\').Length == 0 ? "\\" : filter.Folder.TrimEnd('\\');
        var all = session.List(folder, filter.Recursive, skipMicrosoft: !filter.ShowsMicrosoft).Where(t => TaskAccess.Visible(source.Computer, t.Path)).ToList();
        return (all.Where(filter.Passes).OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList(), all.Count);
    }
}

/// <summary><c>task_info(path, computer?)</c> (2026-10-15): one task whole.</summary>
public sealed class TaskInfoTool(TaskAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : TaskTool(access, effective, forceRedacted)
{
    public const string ToolName = "task_info";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The task's path as task_list shows it, e.g. \"\\Backup\\Nightly\"." },
            "computer": { "type": "string", "description": "Which computer; leave out for this PC (or the only one offered)." }
          },
          "required": ["path"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads one scheduled task in full: when it runs (its triggers in words), what it runs, who it runs as, its last result and its running instances.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new TaskInfoTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, PathArgument) is not { } given)
        {
            return TaskText.Missing(PathArgument);
        }

        string path = TaskPath(given);
        if (TaskAccess.PathProblem(path) is { } bad)
        {
            return bad;
        }

        var (source, refused) = Access.Computer(Optional(arguments, ComputerArgument), ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        if (!TaskAccess.Visible(source.Computer, path))
        {
            return ComputerText.NotVisible(path, source.Where, TaskAccess.Family);
        }

        var result = await Access.ReadAsync(source, path, session => (Detail: session.Detail(path), session.Self), cancellationToken).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            return error;
        }

        var (detail, self) = result.Value;
        // The task Windows found is judged again by its own path (2026-10-20, a review), as a change's is.
        if (!TaskAccess.Visible(source.Computer, detail.Entry.Path))
        {
            return ComputerText.NotVisible(path, source.Where, TaskAccess.Family);
        }

        TaskDefinition? definition = null;
        string? definitionError = null;
        try
        {
            definition = TaskXml.Parse(detail.Xml);
        }
        catch (TaskXmlException ex)
        {
            definitionError = ex.Message;
        }

        return TaskRender.Detail(source.Where, detail, definition, definitionError, source.Redacted, self);
    }
}

/// <summary><c>task_control(path, action, computer?)</c> (2026-10-15): run, end, enable or disable a task, after the order's checks and the user's yes.</summary>
public sealed class TaskControlTool(TaskAccess access, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm, IReadOnlyList<string>? agreed = null)
    : TaskTool(access, effective, forceRedacted: false)
{
    public const string ToolName = "task_control";
    public const string ActionArgument = "action";

    private JsonElement? _schema;

    public override string Name => ToolName;

    /// <summary>The actions every ticked readwrite computer allows (all four when they disagree or none is ticked): the turn's, else read now.</summary>
    public IReadOnlyList<string> AgreedActions() => agreed ?? Access.Catalog().AgreedActions(TaskActions.All);

    /// <summary>This tool for one turn (2026-10-20, <see cref="ServiceControlTool.ForCatalog"/>'s twin): its actions agreed from <paramref name="catalog"/>.</summary>
    public TaskControlTool ForCatalog(ComputerCatalog<TaskComputerConfig> catalog) => new(Access, Effective, confirm, catalog.AgreedActions(TaskActions.All));

    public override string Description => $"Changes a scheduled task: {string.Join(", ", AgreedActions())}. Each call asks the user first; Windows' own tasks and privileged ones are guarded.";

    // The listing's copy parses its schema again only when the agreed actions change (2026-10-23, the admin review's R13).
    public override JsonElement JsonSchema => agreed is null ? Listed(AgreedActions()) : _schema ??= Schema(agreed);

    private (string Key, JsonElement Schema)? _listed;

    private JsonElement Listed(IReadOnlyList<string> actions)
    {
        string key = string.Join(",", actions);
        if (_listed is { } cached && cached.Key == key)
        {
            return cached.Schema;
        }

        var schema = Schema(actions);
        _listed = (key, schema);
        return schema;
    }

    private static JsonElement Schema(IReadOnlyList<string> actions) => ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": {" +
        " \"path\": { \"type\": \"string\", \"description\": \"The task's path as task_list shows it.\" }," +
        " \"action\": { \"type\": \"string\", \"enum\": [" + string.Join(", ", actions.Select(a => "\"" + a + "\"")) + "] }," +
        " \"computer\": { \"type\": \"string\", \"description\": \"Which computer; leave out for this PC (or the only one offered).\" } }," +
        " \"required\": [\"path\", \"action\"] }");

    public override AIFunction AsRedacted() => this;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, PathArgument) is not { } given)
        {
            return TaskText.Missing(PathArgument);
        }

        string action = Optional(arguments, ActionArgument)?.ToLowerInvariant() ?? "";
        if (!TaskActions.All.Contains(action))
        {
            return action.Length == 0 ? TaskText.Missing(ActionArgument) : TaskText.BadAction(action);
        }

        // The checks that need no password first (2026-10-23, the admin review's R6: a read-only computer whose password was gone answered
        // "cannot sign in", and a secret was decrypted for a call refused anyway).
        var (found, unknown) = Access.Catalog().Find(Optional(arguments, ComputerArgument));
        if (found is null)
        {
            return unknown;
        }

        if (TaskAccess.WriteRefusal(found, TaskPath(given), action, null, Effective().TaskWrites) is { } early)
        {
            return early;
        }

        var (source, refused) = Access.Computer(found.Name, forceRedacted: false);
        if (source is null)
        {
            return refused;
        }

        return await TaskChanges.RunAsync(Access, source, TaskPath(given), action, Effective().TaskWrites, byUser: false, confirm, cancellationToken).ConfigureAwait(false);
    }
}
