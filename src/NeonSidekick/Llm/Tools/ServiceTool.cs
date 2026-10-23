using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Services;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// What the four services tools share (2026-10-15, the admin-tools plan, <see cref="EventLogTool"/>'s shape): the <see cref="ServiceAccess"/>
/// door, the settings in force, the arguments' readers, and the redacted twin (<see cref="IRedactedTwin"/>: the bots', the advisors', a
/// script's read every computer redacted). The two reads are offered headless too; the two changes only with <c>Service writes</c>, a pane and
/// a ticked <c>readwrite</c> computer whose <c>controlActions</c> allow them — never headless, never to a bot. A refusal is an <c>Error:</c>
/// sentence, never a throw.
/// </summary>
public abstract class ServiceTool : AIFunction, IRedactedTwin
{
    public const string NameArgument = "name";
    public const string ComputerArgument = "computer";

    protected ServiceTool(ServiceAccess access, Func<AppSettingsData> effective, bool forceRedacted)
    {
        Access = access ?? throw new ArgumentNullException(nameof(access));
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        ForceRedacted = forceRedacted;
    }

    protected ServiceAccess Access { get; }

    protected Func<AppSettingsData> Effective { get; }

    public bool ForceRedacted { get; }

    /// <summary>The same tool reading every computer redacted; itself when it already does (or when it changes something, never a twin's).</summary>
    public abstract AIFunction AsRedacted();

    // The shared readers (2026-10-23, the admin review's R18, E7, K6: AdminArguments).
    protected static string? Optional(AIFunctionArguments arguments, string name) => AdminArguments.Optional(arguments, name);

    protected static (bool Value, string? Error) Flag(AIFunctionArguments arguments, string name, bool fallback) => AdminArguments.Flag(arguments, name, fallback);

    protected static (int Value, string? Error) Number(AIFunctionArguments arguments, string name, int min, int max, int fallback) => AdminArguments.Number(arguments, name, min, max, fallback);
}

/// <summary><c>service_list(computer?, state?, start?, filter?, drivers?, limit?, offset?)</c> (2026-10-15): the services on a computer.</summary>
public sealed class ServiceListTool(ServiceAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : ServiceTool(access, effective, forceRedacted)
{
    public const string ToolName = "service_list";
    public const string StateArgument = "state";
    public const string StartArgument = "start";
    public const string FilterArgument = "filter";
    public const string DriversArgument = "drivers";
    public const string LimitArgument = "limit";
    public const string OffsetArgument = "offset";
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "computer": { "type": "string", "description": "Which computer, as the ticked ones are named; leave out for this PC (or the only one offered)." },
            "state": { "type": "string", "description": "running, stopped or failed (stopped with an error)." },
            "start": { "type": "string", "description": "automatic, delayed, manual, disabled, boot or system (boot and system with drivers)." },
            "filter": { "type": "string", "description": "Only services whose name or display name contains this text." },
            "drivers": { "type": "boolean", "description": "Also list drivers (default false)." },
            "limit": { "type": "integer", "description": "The most to list, 1 to 500 (default 50)." },
            "offset": { "type": "integer", "description": "Skip this many matches (the answer's next call says)." }
          }
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Lists the Windows services on a computer with their states, filtered by state, start type or name.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new ServiceListTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? state = Optional(arguments, StateArgument)?.ToLowerInvariant();
        if (state is not null && !ServiceFilter.States.Contains(state))
        {
            return ServiceText.BadState(state);
        }

        string? start = Optional(arguments, StartArgument)?.ToLowerInvariant();
        if (start is not null && !ServiceFilter.Starts.Contains(start))
        {
            return ServiceText.BadStart(start);
        }

        var (drivers, driversError) = Flag(arguments, DriversArgument, fallback: false);
        var (limit, limitError) = Number(arguments, LimitArgument, 1, MaxLimit, DefaultLimit);
        var (offset, offsetError) = Number(arguments, OffsetArgument, 0, 100_000, 0);
        if ((driversError ?? limitError ?? offsetError) is { } argumentError)
        {
            return argumentError;
        }

        string? computer = Optional(arguments, ComputerArgument);
        var (source, refused) = Access.Computer(computer, ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        var filter = new ServiceFilter(state, start, Optional(arguments, FilterArgument), drivers);
        var result = await Access.ReadAsync(source, null, session => Matches(session, source, filter, offset + limit), cancellationToken).ConfigureAwait(false);
        return result.Error ?? ServiceRender.List(source.Where, result.Value.Matched, result.Value.Total, offset, limit, filter, computer, result.Value.Complete);
    }

    /// <summary>
    /// The visible services that pass the filter, by name; the start filter read per service only when given, in name order and only until
    /// <paramref name="enough"/> have matched and one more (2026-10-23, the admin review's A7: every service's start type was read before the
    /// page, 2–3 calls each, and a remote computer's 300 services ran past the timeout). <c>Complete</c> false when that read stopped early.
    /// </summary>
    public static (IReadOnlyList<ServiceEntry> Matched, int Total, bool Complete) Matches(IServiceSession session, ServiceSource source, ServiceFilter filter, int enough = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        var all = session.List(filter.Drivers).Where(e => ServiceAccess.Visible(source.Computer, e.Name)).ToList();
        var candidates = all.Where(filter.Passes).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (filter.Start is null)
        {
            return (candidates, all.Count, true);
        }

        var matched = new List<ServiceEntry>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (matched.Count > enough)
            {
                return (matched, all.Count, false);
            }

            try
            {
                var (kind, delayed) = session.StartOf(candidates[i].Name);
                if (filter.PassesStart(kind, delayed))
                {
                    matched.Add(candidates[i]);
                }
            }
            catch (ServiceException)
            {
                // A service that will not say its start type is no match.
            }
        }

        return (matched, all.Count, true);
    }
}

/// <summary><c>service_info(name, computer?)</c> (2026-10-15): one service whole.</summary>
public sealed class ServiceInfoTool(ServiceAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : ServiceTool(access, effective, forceRedacted)
{
    public const string ToolName = "service_info";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The service's name (not its display name), as service_list shows it." },
            "computer": { "type": "string", "description": "Which computer; leave out for this PC (or the only one offered)." }
          },
          "required": ["name"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Reads one Windows service in full: its state, start type, account, program, recovery actions, what it depends on and what depends on it.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => ForceRedacted ? this : new ServiceInfoTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return ServiceText.Missing(NameArgument);
        }

        if (ServiceAccess.NameProblem(name) is { } bad)
        {
            return bad;
        }

        var (source, refused) = Access.Computer(Optional(arguments, ComputerArgument), ForceRedacted);
        if (source is null)
        {
            return refused;
        }

        if (!ServiceAccess.Visible(source.Computer, name))
        {
            return ComputerText.NotVisible(name, source.Where, ServiceAccess.Family);
        }

        var result = await Access.ReadAsync(source, name, session => session.Detail(name), cancellationToken).ConfigureAwait(false);
        return result.Error ?? ServiceRender.Detail(source.Where, result.Value!, source.Redacted, n => ServiceAccess.Visible(source.Computer, n));
    }
}

/// <summary>
/// <c>service_control(name, action, computer?)</c> (2026-10-15): start, stop or restart a service, after every check of the order and the
/// user's yes. Its <c>action</c> enum and description list only the actions every ticked readwrite computer allows, when they agree. A turn's
/// copy (<see cref="ForCatalog"/>, 2026-10-20, a review: each request's read of the description and schema read both files again) holds them
/// fixed from the turn's catalog, its schema parsed once.
/// </summary>
public sealed class ServiceControlTool(ServiceAccess access, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm, IReadOnlyList<string>? agreed = null)
    : ServiceTool(access, effective, forceRedacted: false)
{
    public const string ToolName = "service_control";
    public const string ActionArgument = "action";

    private JsonElement? _schema;

    public override string Name => ToolName;

    /// <summary>The control actions every ticked readwrite computer allows (all three when they disagree or none is ticked): the turn's, else read now.</summary>
    public IReadOnlyList<string> AgreedActions() => agreed ?? Access.Catalog().AgreedActions(ServiceActions.Control);

    /// <summary>This tool for one turn: its actions agreed from <paramref name="catalog"/>, the turn's own read.</summary>
    public ServiceControlTool ForCatalog(ComputerCatalog<ServiceComputerConfig> catalog) => new(Access, Effective, confirm, catalog.AgreedActions(ServiceActions.Control));

    // A driver is refused for every action, a protected service for stop and restart (2026-10-23, the admin review's R8: it said only "stopped").
    public override string Description => $"Changes whether a Windows service runs: {string.Join(", ", AgreedActions())}. Each call asks the user first. A driver is never changed, and a protected service is never stopped or restarted.";

    // The listing's copy (agreed null: /tools, /sysprompt) parses its schema again only when the agreed actions change (2026-10-23, R13:
    // each draw parsed a new JsonDocument).
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
        " \"name\": { \"type\": \"string\", \"description\": \"The service's name, as service_list shows it.\" }," +
        " \"action\": { \"type\": \"string\", \"enum\": [" + string.Join(", ", actions.Select(a => "\"" + a + "\"")) + "] }," +
        " \"computer\": { \"type\": \"string\", \"description\": \"Which computer; leave out for this PC (or the only one offered).\" } }," +
        " \"required\": [\"name\", \"action\"] }");

    public override AIFunction AsRedacted() => this;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return ServiceText.Missing(NameArgument);
        }

        string action = Optional(arguments, ActionArgument)?.ToLowerInvariant() ?? "";
        if (!ServiceActions.Control.Contains(action))
        {
            return action.Length == 0 ? ServiceText.Missing(ActionArgument) : ServiceText.BadAction(action);
        }

        // The checks that need no password first (2026-10-23, the admin review's R6: a read-only computer whose password was gone answered
        // "cannot sign in", and a secret was decrypted for a call refused anyway).
        var (found, unknown) = Access.Catalog().Find(Optional(arguments, ComputerArgument));
        if (found is null)
        {
            return unknown;
        }

        if (ServiceAccess.WriteRefusal(found, name, action, null, Effective().ServiceWrites) is { } early)
        {
            return early;
        }

        var (source, refused) = Access.Computer(found.Name, forceRedacted: false);
        if (source is null)
        {
            return refused;
        }

        return await ServiceChanges.RunAsync(Access, source, name, action, null, Effective().ServiceWrites, byUser: false, confirm, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary><c>service_start_type(name, start_type, computer?)</c> (2026-10-15): automatic, delayed, manual or disabled, after the order's checks and the user's yes.</summary>
public sealed class ServiceStartTypeTool(ServiceAccess access, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm) : ServiceTool(access, effective, forceRedacted: false)
{
    public const string ToolName = "service_start_type";
    public const string StartTypeArgument = "start_type";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The service's name, as service_list shows it." },
            "start_type": { "type": "string", "enum": ["automatic", "delayed", "manual", "disabled"] },
            "computer": { "type": "string", "description": "Which computer; leave out for this PC (or the only one offered)." }
          },
          "required": ["name", "start_type"]
        }
        """);

    public override string Name => ToolName;

    public override string Description => "Changes how a Windows service starts: automatic, delayed, manual or disabled. Each call asks the user first.";

    public override JsonElement JsonSchema => Schema;

    public override AIFunction AsRedacted() => this;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Optional(arguments, NameArgument) is not { } name)
        {
            return ServiceText.Missing(NameArgument);
        }

        string word = Optional(arguments, StartTypeArgument) ?? "";
        if (ServiceChanges.Choice(word) is not { } choice)
        {
            return word.Length == 0 ? ServiceText.Missing(StartTypeArgument) : ServiceText.BadStartChoice(word);
        }

        // The checks that need no password first (2026-10-23, the admin review's R6: a read-only computer whose password was gone answered
        // "cannot sign in", and a secret was decrypted for a call refused anyway).
        var (found, unknown) = Access.Catalog().Find(Optional(arguments, ComputerArgument));
        if (found is null)
        {
            return unknown;
        }

        if (ServiceAccess.WriteRefusal(found, name, ServiceActions.StartType, null, Effective().ServiceWrites) is { } early)
        {
            return early;
        }

        var (source, refused) = Access.Computer(found.Name, forceRedacted: false);
        if (source is null)
        {
            return refused;
        }

        return await ServiceChanges.RunAsync(Access, source, name, ServiceActions.StartType, choice, Effective().ServiceWrites, byUser: false, confirm, cancellationToken).ConfigureAwait(false);
    }
}
