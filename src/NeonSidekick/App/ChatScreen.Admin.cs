using Microsoft.Extensions.AI;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Network;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// One admin family's group as <c>/tools</c> and <c>/sysprompt</c> list it (2026-10-15, the admin-tools plan): its title, its tools, whether it
/// is offered, the reason when it is not, its note when it is (a directory with no domain), and its tools' own notes (a write not offered and
/// why, <c>net_certs</c> behind its switch, no ping here). <see cref="SystemPromptSummary.ToolGroups"/> makes a group of each.
/// </summary>
public sealed record AdminGroupSpec(string Title, IReadOnlyList<AIFunction> Tools, bool Enabled, string OffReason, SettingsField Switch, IReadOnlyDictionary<string, string>? ToolNotes = null, string? OnNote = null);

// ── The admin families (2026-10-15, the admin-tools plan): network, services, tasks, the directory ──────────────────

internal sealed partial class ChatScreen
{
    private NetworkAccess _network = null!;

    /// <summary>The main chat's scan budget: reset as each turn's tools are composed; scripts' and advisors' calls count against it too.</summary>
    private readonly NetworkBudget _networkBudget = new();

    /// <summary>The bots' scan budget: reset at each bot turn.</summary>
    private readonly NetworkBudget _networkBotBudget = new();

    private IReadOnlyList<AIFunction> _networkTools = [];
    private IReadOnlyList<AIFunction> _networkBotTools = [];
    private ServiceAccess _services = null!;
    private IReadOnlyList<AIFunction> _serviceTools = [];
    private IReadOnlyList<AIFunction> _serviceBotTools = [];
    private TaskAccess _tasks = null!;
    private IReadOnlyList<AIFunction> _taskTools = [];
    private IReadOnlyList<AIFunction> _taskBotTools = [];
    private DirectoryAccess _directory = null!;
    private IReadOnlyList<AIFunction> _directoryTools = [];
    private IReadOnlyList<AIFunction> _directoryBotTools = [];

    /// <summary>The four families built over their doors (the constructor's call): the main chat's tools and the bots' redacted twins.</summary>
    private void InitAdmin(IPinger? pinger, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? resolve, IServiceSystem? serviceSystem, ITaskSystem? taskSystem, IDirectorySystem? directorySystem, Func<string?>? joinedDomain)
    {
        _network = new NetworkAccess(_effective, pinger, _time, resolve);
        _networkTools = NetworkTools(_network, _networkBudget);
        _networkBotTools = NetworkTools(_network, _networkBotBudget);
        _services = new ServiceAccess(serviceSystem, () => ServiceConfigFile.Family.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory), () => _effective().ServiceComputersOffered, _time);
        _serviceTools = ServiceTools(_services, _effective, ConfirmAdminAsync);
        _serviceBotTools = ServiceTools(_services, _effective, null, forceRedacted: true);
        _tasks = new TaskAccess(taskSystem, () => TaskConfigFile.Family.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory), () => _effective().TaskComputersOffered, _time);
        _taskTools = TaskTools(_tasks, _effective, ConfirmAdminAsync);
        _taskBotTools = TaskTools(_tasks, _effective, null, forceRedacted: true);
        var joined = joinedDomain ?? (() => DomainJoin.Current);
        _directory = new DirectoryAccess(directorySystem, () => DirectoryConfigFile.LoadCatalog(_settings.ProfileDirectory, _settings.StorageDirectory, joined()), () => _effective().DirectoryDomainsOffered, joined, _time);
        _directoryTools = DirectoryTools(_directory);
        _directoryBotTools = DirectoryTools(_directory, forceRedacted: true);
    }

    // ── The tools ───────────────────────────────────────────────────────────

    /// <summary>The six network tools over <paramref name="access"/>, counting against <paramref name="budget"/>. Shared with headless.</summary>
    public static IReadOnlyList<AIFunction> NetworkTools(NetworkAccess access, NetworkBudget budget) =>
    [
        new NetDnsTool(access, budget),
        new NetPingTool(access, budget),
        new NetTraceTool(access, budget),
        new NetPortTool(access, budget),
        new NetTlsTool(access, budget),
        new NetCertsTool(access, budget),
    ];

    /// <summary>The services tools: the two reads, and the two changes unless <paramref name="forceRedacted"/> (a bot's set). Shared with headless (the changes never offered there).</summary>
    public static IReadOnlyList<AIFunction> ServiceTools(ServiceAccess access, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm, bool forceRedacted = false) =>
        forceRedacted
            ? [new ServiceListTool(access, effective, true), new ServiceInfoTool(access, effective, true)]
            : [new ServiceListTool(access, effective), new ServiceInfoTool(access, effective), new ServiceControlTool(access, effective, confirm), new ServiceStartTypeTool(access, effective, confirm)];

    /// <summary>The task tools: the two reads, and <c>task_control</c> unless <paramref name="forceRedacted"/>.</summary>
    public static IReadOnlyList<AIFunction> TaskTools(TaskAccess access, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm, bool forceRedacted = false) =>
        forceRedacted
            ? [new TaskListTool(access, effective, true), new TaskInfoTool(access, effective, true)]
            : [new TaskListTool(access, effective), new TaskInfoTool(access, effective), new TaskControlTool(access, effective, confirm)];

    /// <summary>The five directory tools, every one a read.</summary>
    public static IReadOnlyList<AIFunction> DirectoryTools(DirectoryAccess access, bool forceRedacted = false) =>
    [
        new AdUserTool(access, forceRedacted),
        new AdGroupTool(access, forceRedacted),
        new AdComputerTool(access, forceRedacted),
        new AdFindTool(access, forceRedacted),
        new AdDomainTool(access, forceRedacted),
    ];

    public static readonly IReadOnlySet<string> NetworkToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        NetDnsTool.ToolName, NetPingTool.ToolName, NetTraceTool.ToolName, NetPortTool.ToolName, NetTlsTool.ToolName, NetCertsTool.ToolName,
    };

    public static readonly IReadOnlySet<string> ServiceToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ServiceListTool.ToolName, ServiceInfoTool.ToolName, ServiceControlTool.ToolName, ServiceStartTypeTool.ToolName,
    };

    public static readonly IReadOnlySet<string> ServiceWriteToolNames = new HashSet<string>(StringComparer.Ordinal) { ServiceControlTool.ToolName, ServiceStartTypeTool.ToolName };

    public static readonly IReadOnlySet<string> TaskToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskListTool.ToolName, TaskInfoTool.ToolName, TaskControlTool.ToolName,
    };

    public static readonly IReadOnlySet<string> DirectoryToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        AdUserTool.ToolName, AdGroupTool.ToolName, AdComputerTool.ToolName, AdFindTool.ToolName, AdDomainTool.ToolName,
    };

    // ── When each is offered ────────────────────────────────────────────────

    /// <summary>The network group: <c>Network tools</c> on (the Mac too).</summary>
    public static bool NetworkOffered(AppSettingsData effective) => effective.NetworkTools;

    /// <summary>The services group: <c>Service tools</c> on, on Windows, with a Service Control Manager.</summary>
    public static bool ServicesOffered(AppSettingsData effective, ServiceAccess access) => effective.ServiceTools && OperatingSystem.IsWindows() && access.System is not null;

    /// <summary>The task group: <c>Task tools</c> on, on Windows, with a Task Scheduler.</summary>
    public static bool TasksOffered(AppSettingsData effective, TaskAccess access) => effective.TaskTools && OperatingSystem.IsWindows() && access.System is not null;

    /// <summary>The directory group: <c>Directory tools</c> on, on Windows, with a directory and at least one domain ticked.</summary>
    public static bool DirectoryOffered(AppSettingsData effective, DirectoryAccess access) =>
        effective.DirectoryTools && OperatingSystem.IsWindows() && access.System is not null && access.Catalog().Domains.Count > 0;

    /// <summary>
    /// What a turn may offer of the network tools: <c>net_certs</c> only with <c>Network certificate stores</c> on (the user's call), ping and
    /// trace only with a pinger (trace only where a hop's answer reaches it). Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> NetworkToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective, IPinger? pinger) =>
        tools.Where(t => t switch
        {
            NetCertsTool => effective.NetworkCertificateStores,
            NetPingTool => pinger is not null,
            NetTraceTool => pinger is { CanTrace: true },
            _ => true,
        }).ToList();

    /// <summary>
    /// What a turn may offer of the services tools (2026-10-15, the plan's offering rule): the reads always; a change only with <c>Service writes</c>
    /// on, a pane to ask on, and a ticked <c>readwrite</c> computer whose <c>controlActions</c> allow it (<c>service_start_type</c> needs
    /// <c>start_type</c>). Pure over the catalog.
    /// </summary>
    public static IReadOnlyList<AIFunction> ServiceToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective, bool pane, ComputerCatalog<ServiceComputerConfig> catalog) =>
        tools.Where(t => !ServiceWriteToolNames.Contains(t.Name) || ServiceWriteReason(t.Name, effective, pane, catalog) is null).ToList();

    /// <summary>Why a services change tool is not offered (the plan's item 30), or null when it is.</summary>
    public static string? ServiceWriteReason(string tool, AppSettingsData effective, bool pane, ComputerCatalog<ServiceComputerConfig> catalog)
    {
        if (!effective.ServiceWrites)
        {
            return ToolsText.SwitchOffReason(SettingsField.ServiceWrites);
        }

        if (!pane)
        {
            return ToolsText.NoPaneReason;
        }

        var readWrite = catalog.Computers.Where(c => c.Config.ReadWrite).ToList();
        if (readWrite.Count == 0)
        {
            return ToolsText.NoReadWriteComputerReason;
        }

        string[] actions = tool == ServiceStartTypeTool.ToolName ? [ServiceActions.StartType] : [.. ServiceActions.Control];
        return readWrite.Any(c => actions.Any(c.Config.AllowsAction)) ? null
            : tool == ServiceStartTypeTool.ToolName ? ToolsText.NoStartTypeAllowedReason : ToolsText.NoActionAllowedReason;
    }

    /// <summary>What a turn may offer of the task tools: <c>task_control</c> as the services' changes are.</summary>
    public static IReadOnlyList<AIFunction> TaskToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective, bool pane, ComputerCatalog<TaskComputerConfig> catalog) =>
        tools.Where(t => t is not TaskControlTool || TaskWriteReason(effective, pane, catalog) is null).ToList();

    /// <summary>Why <c>task_control</c> is not offered, or null when it is.</summary>
    public static string? TaskWriteReason(AppSettingsData effective, bool pane, ComputerCatalog<TaskComputerConfig> catalog)
    {
        if (!effective.TaskWrites)
        {
            return ToolsText.SwitchOffReason(SettingsField.TaskWrites);
        }

        if (!pane)
        {
            return ToolsText.NoPaneReason;
        }

        var readWrite = catalog.Computers.Where(c => c.Config.ReadWrite).ToList();
        return readWrite.Count == 0 ? ToolsText.NoReadWriteComputerReason
            : readWrite.Any(c => c.Config.ControlActions is not { Count: 0 }) ? null
            : ToolsText.NoActionAllowedReason;
    }

    /// <summary>
    /// The four families as <c>/tools</c> and <c>/sysprompt</c> list them (2026-10-15): every tool in its group, each not offered with its reason
    /// as a note; a group off with its own reason (the switch, else "there are no … here" off Windows).
    /// </summary>
    private IReadOnlyList<AdminGroupSpec> AdminGroups(AppSettingsData effective)
    {
        var networkNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!effective.NetworkCertificateStores)
        {
            networkNotes[NetCertsTool.ToolName] = SystemPromptSummary.NotOffered(NetworkText.CertStoresOffReason);
        }

        if (_network.Pinger is null)
        {
            networkNotes[NetPingTool.ToolName] = SystemPromptSummary.NotOffered(ToolsText.NoPingReason);
        }

        if (_network.Pinger is not { CanTrace: true })
        {
            networkNotes[NetTraceTool.ToolName] = SystemPromptSummary.NotOffered(ToolsText.NoTraceReason);
        }

        var serviceNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var serviceCatalog = _services.Catalog();
        foreach (string write in ServiceWriteToolNames)
        {
            if (ServiceWriteReason(write, effective, _pane.Enabled, serviceCatalog) is { } why)
            {
                serviceNotes[write] = SystemPromptSummary.NotOffered(why);
            }
        }

        var taskNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TaskWriteReason(effective, _pane.Enabled, _tasks.Catalog()) is { } taskWhy)
        {
            taskNotes[TaskControlTool.ToolName] = SystemPromptSummary.NotOffered(taskWhy);
        }

        bool windows = OperatingSystem.IsWindows();
        bool directoryOn = effective.DirectoryTools && windows && _directory.System is not null;
        return
        [
            new AdminGroupSpec(ToolsText.NetworkMenuTitle, _networkTools, NetworkOffered(effective), ToolsText.SwitchOffReason(SettingsField.NetworkTools), SettingsField.NetworkTools, networkNotes),
            new AdminGroupSpec(ToolsText.ServicesMenuTitle, _serviceTools, ServicesOffered(effective, _services),
                !effective.ServiceTools ? ToolsText.SwitchOffReason(SettingsField.ServiceTools) : ServiceText.NoneHereReason, SettingsField.ServiceTools, serviceNotes),
            new AdminGroupSpec(ToolsText.TasksMenuTitle, _taskTools, TasksOffered(effective, _tasks),
                !effective.TaskTools ? ToolsText.SwitchOffReason(SettingsField.TaskTools) : TaskText.NoneHereReason, SettingsField.TaskTools, taskNotes),
            new AdminGroupSpec(ToolsText.DirectoryMenuTitle, _directoryTools, DirectoryOffered(effective, _directory),
                !effective.DirectoryTools ? ToolsText.SwitchOffReason(SettingsField.DirectoryTools) : !directoryOn ? DirectoryText.NoneHereReason : DirectoryText.NoDomainReason, SettingsField.DirectoryTools),
        ];
    }

    /// <summary>The four families' inputs for a turn (the main chat's), each list cut by its rules.</summary>
    private TurnToolInputs WithAdmin(TurnToolInputs inputs, AppSettingsData effective) => inputs with
    {
        Network = NetworkToolsFor(_networkTools, effective, _network.Pinger),
        NetworkEnabled = NetworkOffered(effective),
        Services = ServiceToolsFor(_serviceTools, effective, _pane.Enabled, _services.Catalog()),
        ServicesEnabled = ServicesOffered(effective, _services),
        Tasks = TaskToolsFor(_taskTools, effective, _pane.Enabled, _tasks.Catalog()),
        TasksEnabled = TasksOffered(effective, _tasks),
        Directory = _directoryTools,
        DirectoryEnabled = DirectoryOffered(effective, _directory),
    };

    /// <summary>The four families' inputs for a bot's turn: the redacted reads alone, the bots' own scan budget, never a change.</summary>
    private TurnToolInputs WithAdminForBot(TurnToolInputs inputs, AppSettingsData effective)
    {
        _networkBotBudget.Reset();
        return inputs with
        {
            Network = NetworkToolsFor(_networkBotTools, effective, _network.Pinger),
            NetworkEnabled = NetworkOffered(effective),
            Services = _serviceBotTools,
            ServicesEnabled = ServicesOffered(effective, _services),
            Tasks = _taskBotTools,
            TasksEnabled = TasksOffered(effective, _tasks),
            Directory = _directoryBotTools,
            DirectoryEnabled = DirectoryOffered(effective, _directory),
        };
    }

    /// <summary>
    /// A model's service or task change's question, on the turn task (2026-10-15, the plan's item 1): on the PC's pane and the Web UI's page at
    /// once (<see cref="ConfirmBothAsync"/>), the first answer winning; null (never asked) with nobody to ask.
    /// </summary>
    private Task<bool?> ConfirmAdminAsync(string question, CancellationToken turnToken) =>
        ConfirmBothAsync(question, "The admin confirm pane failed: ", turnToken);

    // ── The wizards' tests (the screen's systems) ───────────────────────────

    private Task<(bool Ok, string Text)> TestServiceComputerAsync(Computer<ServiceComputerConfig> computer, CancellationToken cancellationToken) =>
        ServiceAccess.TestAsync(_services.System, computer, _time, cancellationToken);

    private Task<(bool Ok, string Text)> TestTaskComputerAsync(Computer<TaskComputerConfig> computer, CancellationToken cancellationToken) =>
        TaskAccess.TestAsync(_tasks.System, computer, _time, cancellationToken);

    private Task<(bool Ok, string Text)> TestDirectoryDomainAsync(DirectoryDomain domain, CancellationToken cancellationToken) =>
        DirectoryAccess.TestAsync(_directory.System, domain, _time, cancellationToken);

    // ── The transcript's notes ───────────────────────────────────────────────

    /// <summary>
    /// The note for an admin tool's result (its first line, the family's glyph), or null for another tool's. A network budget refusal is noted
    /// once a turn (<see cref="NetworkBudget.NoteOnce"/>), nothing after.
    /// </summary>
    private string? AdminNote(string tool, string text)
    {
        if (NetworkToolNames.Contains(tool))
        {
            if (text.StartsWith("Error: this turn has checked", StringComparison.Ordinal))
            {
                return _networkBudget.NoteOnce() ? NetworkText.BudgetNote : "";
            }

            return NetworkText.Note(text);
        }

        return ServiceToolNames.Contains(tool) ? ServiceText.Note(text)
            : TaskToolNames.Contains(tool) ? TaskText.Note(text)
            : DirectoryToolNames.Contains(tool) ? DirectoryText.Note(text)
            : null;
    }

    // ── /net ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/net …</c> (2026-10-15): the user's own hand over the network checks, never judged by the reach, the lists or the budget, working with the
    /// tools off; its lines on the list pane (a card on the Web UI's page, lines headless), a dim note when the model would be refused.
    /// </summary>
    private async Task HandleNetAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        var result = onWatcher
            ? await NetworkCommand.RunAsync(_network, args, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(NetworkText.Working, () => NetworkCommand.RunAsync(_network, args, cancellationToken)).ConfigureAwait(false);
        if (result.Failed)
        {
            foreach (string failed in result.Lines)
            {
                sink.Error(failed);
            }

            return;
        }

        var lines = result.Note is { } note ? [.. result.Lines, note] : result.Lines;
        await ShowLinesAsync(TreeText.PaneLabel(NetworkCommand.Word, args), lines, sink, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<CompletionItem> NetChoices(string argText) => NetworkCommand.Complete(argText);

    // ── /services, /tasks, /ad ──────────────────────────────────────────────

    /// <summary>A draft from an admin pane under a reply (an <c>ask</c>), kept for the hook to put on the line after the pane phase.</summary>
    private string? _adminDraft;

    /// <summary>After a pane phase under a reply: an admin pane's ask goes on the draft now, as the event log's does.</summary>
    private bool TakeAdminDraft(bool typed)
    {
        if (_adminDraft is not { } draft)
        {
            return true;
        }

        _adminDraft = null;
        FillDraft(draft, keepOld: !typed);
        return !typed;
    }

    /// <summary>A draft from a pane: on the line at once at the idle line, kept for after the reply under one.</summary>
    private void LeaveDraft(string draft, bool onWatcher)
    {
        if (onWatcher)
        {
            _adminDraft = draft;
        }
        else
        {
            FillDraft(draft, keepOld: true);
        }
    }

    /// <summary>The pane's own yes/no for a change by the user's hand (the cursor on No, ESC a no).</summary>
    private async Task<bool?> ConfirmOnPaneAsync(string question, CancellationToken cancellationToken) =>
        await _menu.ConfirmAsync(question, cancellationToken, close: false).ConfigureAwait(false);

    /// <summary>A pick among <paramref name="choices"/> on the pane, for the start type.</summary>
    private Task<int?> ChooseOnPaneAsync(IReadOnlyList<string> choices, string title, CancellationToken cancellationToken) =>
        _menu.ChooseAsync(new MenuPage(title, choices.Select(Spectre.Console.Markup.Escape).ToList(), SettingsMenu.PickKeys), cancellationToken);

    /// <summary>
    /// <c>/services [line]</c> (2026-10-15): on the pane <see cref="ServicesMenu"/>; without it, or from the Web UI's page, the lines on the list
    /// pane. Works with the tools off; a change from the pane is the user's own hand.
    /// </summary>
    private async Task HandleServicesAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        if (!OperatingSystem.IsWindows() || _services.System is null)
        {
            sink.Error(ServiceText.NoneHere);
            return;
        }

        if (_pane.Enabled && !FromWebNow)
        {
            var (line, error) = ServiceCommand.Parse(args);
            if (line is null)
            {
                sink.Error(error!);
                return;
            }

            var menu = new ServicesMenu(_services, _effective, sink, _menuPane, _info, _copy, () => _pane.Profile.Width, ConfirmOnPaneAsync, ChooseOnPaneAsync);
            if (await menu.ShowAsync(line, cancellationToken).ConfigureAwait(false) is { } draft)
            {
                LeaveDraft(draft, onWatcher);
            }

            return;
        }

        var result = onWatcher
            ? await ServiceCommand.RunAsync(_services, args, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(ServiceText.Working, () => ServiceCommand.RunAsync(_services, args, cancellationToken)).ConfigureAwait(false);
        await ShowCommandLinesAsync(ServiceCommand.Word, args, result.Lines, result.Failed, sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/tasks [line]</c> (2026-10-15): on the pane <see cref="TasksMenu"/> (its History opens <c>/eventlog</c>); elsewhere the lines.</summary>
    private async Task HandleTasksAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        if (!OperatingSystem.IsWindows() || _tasks.System is null)
        {
            sink.Error(TaskText.NoneHere);
            return;
        }

        if (_pane.Enabled && !FromWebNow)
        {
            var (line, error) = TaskCommand.Parse(args);
            if (line is null)
            {
                sink.Error(error!);
                return;
            }

            var menu = new TasksMenu(_tasks, _effective, sink, _menuPane, _info, _copy, () => _pane.Profile.Width, ConfirmOnPaneAsync, EventLogComputerFor);
            var outcome = await menu.ShowAsync(line, cancellationToken).ConfigureAwait(false);
            if (outcome.Draft is { } draft)
            {
                LeaveDraft(draft, onWatcher);
            }
            else if (outcome.History is { } history)
            {
                await OpenTaskHistoryAsync(history, sink, cancellationToken, onWatcher).ConfigureAwait(false);
            }

            return;
        }

        var result = onWatcher
            ? await TaskCommand.RunAsync(_tasks, args, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(TaskText.Working, () => TaskCommand.RunAsync(_tasks, args, cancellationToken)).ConfigureAwait(false);
        await ShowCommandLinesAsync(TaskCommand.Word, args, result.Lines, result.Failed, sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ticked event-log computer with the same host as <paramref name="computer"/> (both <c>local</c>, or the same host name) — the History
    /// and "Search 4740" buttons' match (the plan's item 18: each family keeps its own names) — or null when there is none, or no event logs here.
    /// </summary>
    private string? EventLogComputerFor(Computer<TaskComputerConfig> computer) => EventLogComputerOn(computer.IsLocal ? null : computer.Config.Host);

    private string? EventLogComputerOn(string? host)
    {
        if (!OperatingSystem.IsWindows() || _eventLog.System is null)
        {
            return null;
        }

        foreach (var c in _eventLog.Catalog().Computers)
        {
            if (host is null ? c.IsLocal : !c.IsLocal && string.Equals(c.Config.Host?.Trim(), host.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return c.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// A task's History (2026-10-15, the plan's item 35): the Task Scheduler's log is off by default, so its enabled state is read first; off, the
    /// sentence says how to turn it on (the app never does: that would be a write to the event logs); on, <c>/eventlog</c> opens on it.
    /// </summary>
    private async Task OpenTaskHistoryAsync(string line, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher)
    {
        string? computer = line.Contains(" computer:", StringComparison.Ordinal) ? line.Split(' ').First(w => w.StartsWith("computer:", StringComparison.Ordinal))[9..] : null;
        var (source, error) = _eventLog.Resolve(computer, TaskText.HistoryLog, forceRedacted: false);
        if (source is null)
        {
            sink.Error(error!);
            return;
        }

        var info = await _eventLog.RunAsync(source, (system, token) => system.Info(source.Target, source.Log, token), cancellationToken).ConfigureAwait(false);
        if (info.Error is null && info.Value is { Enabled: false })
        {
            sink.Notice(TaskText.HistoryOff);
            return;
        }

        await HandleEventLogAsync(line, sink, cancellationToken, onWatcher).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/ad [line]</c> (2026-10-15): the answer as a card on the info pane with copy, ask and — on a locked user whose PDC emulator is a
    /// ticked event-log computer — "Search 4740 on &lt;PDC&gt;"; without the pane, or from the Web UI's page, its lines.
    /// </summary>
    private async Task HandleAdAsync(string args, INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        if (!OperatingSystem.IsWindows() || _directory.System is null)
        {
            sink.Error(DirectoryText.NoneHere);
            return;
        }

        var result = onWatcher
            ? await DirectoryCommand.RunAsync(_directory, args, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(DirectoryText.Working, () => DirectoryCommand.RunAsync(_directory, args, cancellationToken)).ConfigureAwait(false);
        if (!_pane.Enabled || FromWebNow || result.Failed)
        {
            await ShowCommandLinesAsync(DirectoryCommand.Word, args, result.Lines, result.Failed, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        string text = string.Join('\n', result.Lines);
        string? lockoutOn = result.LockedPdc is { } pdc ? EventLogComputerOn(pdc) : null;
        var actions = new List<InfoAction> { new("copy", 'c', "copy"), new("ask", 'a', "ask the model") };
        if (lockoutOn is not null)
        {
            actions.Add(new InfoAction(DirectoryCommand.LockoutButton(result.LockedPdc!), 's', "search the PDC emulator's Security log"));
        }

        string title = DirectoryText.Glyph + " Directory · " + (result.Title ?? args);
        while (true)
        {
            await _info.ShowAsync(title, [new InfoTab(result.Title ?? "answer", () => new Spectre.Console.Text(text))], 0, cancellationToken, actions).ConfigureAwait(false);
            switch (_info.Pressed?.Key)
            {
                case 'c':
                    if (_copy(text))
                    {
                        sink.Notice("(" + DirectoryText.Glyph + " copied)");
                    }

                    continue;
                case 'a':
                    LeaveDraft(DirectoryCommand.AskDraft(result.Title ?? args, _directory.Catalog().Find(DirectoryCommand.Parse(args).Args?.Domain).Domain?.DomainName ?? ""), onWatcher);
                    return;
                case 's':
                    await HandleEventLogAsync(DirectoryCommand.LockoutLine(lockoutOn!), sink, cancellationToken, onWatcher).ConfigureAwait(false);
                    return;
                default:
                    return;
            }
        }
    }

    /// <summary>A command's lines on the list pane (a card from the Web UI, notices without the pane), a failure as an error.</summary>
    private async Task ShowCommandLinesAsync(string word, string args, IReadOnlyList<string> lines, bool failed, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (failed)
        {
            foreach (string line in lines)
            {
                sink.Error(line);
            }

            return;
        }

        await ShowLinesAsync(TreeText.PaneLabel(word, args), lines, sink, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<CompletionItem> ServiceChoices(string argText) =>
        OperatingSystem.IsWindows() && _services.System is not null ? ServiceCommand.Complete(argText, _services.Catalog().Computers.Select(c => c.Name).ToList()) : [];

    private IReadOnlyList<CompletionItem> TaskChoices(string argText) =>
        OperatingSystem.IsWindows() && _tasks.System is not null ? TaskCommand.Complete(argText, _tasks.Catalog().Computers.Select(c => c.Name).ToList()) : [];

    private IReadOnlyList<CompletionItem> AdChoices(string argText) =>
        OperatingSystem.IsWindows() && _directory.System is not null ? DirectoryCommand.Complete(argText, _directory.Catalog().Domains.Select(d => d.Name).ToList()) : [];
}
