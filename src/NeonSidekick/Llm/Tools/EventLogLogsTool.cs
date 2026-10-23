using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>eventlog_logs(filter?, all?, computer?)</c> (2026-10-15, the event-log plan): the computers offered (with their descriptions and
/// detail), then a computer's logs after the name filter and its <c>logs</c> allow-list — at most <see cref="LocalProbes"/> looked into on
/// this PC, <see cref="RemoteProbes"/> on a remote one ("N more not checked; narrow with filter"), the classic logs first — by default the
/// enabled, non-empty ones that are not Analytic or Debug (<c>all</c> lists those too, marked "read oldest-first only"), each with its
/// records, size and last write, or "no access". Always ends with a <i>Files</i> part: the working directory's <c>.evtx</c> files (50 at
/// most). The model's way to a name: the rule says never to guess one.
/// </summary>
public sealed class EventLogLogsTool : EventLogTool
{
    public const string ToolName = "eventlog_logs";
    public const string FilterArgument = "filter";
    public const string AllArgument = "all";

    /// <summary>The most logs looked into on this PC in one call.</summary>
    public const int LocalProbes = 200;

    /// <summary>The most logs looked into on a remote computer in one call.</summary>
    public const int RemoteProbes = 40;

    /// <summary>The most <c>.evtx</c> files listed.</summary>
    public const int MaxFiles = 50;

    /// <summary>How deep under the working directory <c>.evtx</c> files are looked for.</summary>
    public const int FileDepth = 6;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "filter": { "type": "string", "description": "Only logs whose name contains this text (case ignored), e.g. \"PowerShell\" or \"TaskScheduler\"." },
            "all": { "type": "boolean", "description": "Also list the disabled, empty, Analytic and Debug logs (default false)." },
            "computer": { "type": "string", "description": "Whose logs to list, as the answer's Computers offered names them; leave out for this PC (or the only one offered)." }
          }
        }
        """);

    public EventLogLogsTool(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) : base(access, effective, forceRedacted)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the computers whose Windows event logs can be read, a computer's logs with their sizes and last writes, and the .evtx files in the working directory. Call it to find a log's exact name before searching.";

    public override JsonElement JsonSchema => Schema;

    public override EventLogTool AsRedacted() => ForceRedacted ? this : new EventLogLogsTool(Access, Effective, forceRedacted: true);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        string? filter = Optional(arguments, FilterArgument);
        var (all, flagError) = Flag(arguments, AllArgument, fallback: false);
        if (flagError is not null)
        {
            return flagError;
        }

        var offered = Access.Catalog();
        var zone = Zone;
        var (source, refused) = Access.Computer(Optional(arguments, ComputerArgument), ForceRedacted, offered);

        // The working directory's files listed only beside this PC's logs, off the turn's thread and bounded (2026-10-23, the admin review's
        // I11: the walk ran first and synchronously, even for a remote listing).
        var (files, truncated) = source is { Remote: true } ? ([], false) : await FilesAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return EventLogRender.Logs(offered, null, filter, [], 0, refused, files, truncated, zone);
        }

        var result = await Access.RunAsync(source, (system, token) => Probe(system, source, filter, all, token), cancellationToken).ConfigureAwait(false);
        return result.Error is { } error
            ? EventLogRender.Logs(offered, null, filter, [], 0, error, files, truncated, zone)
            : EventLogRender.Logs(offered, source, filter, result.Value.Logs, result.Value.NotChecked, null, files, truncated, zone);
    }

    /// <summary>
    /// The computer's logs as listed: its channels after the filter and its <c>logs</c>, the classic ones (no <c>/</c> in the name) first, each
    /// looked into up to the cap; by default the disabled, the empty and the Analytic and Debug ones left out.
    /// </summary>
    public static (IReadOnlyList<EventLogListing> Logs, int NotChecked) Probe(IEventLogSystem system, EventLogSource source, string? filter, bool all, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(source);
        var names = system.Channels(source.Target, cancellationToken)
            .Where(n => (filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase)) && EventLogCatalog.Allows(source.Computer.Config.Logs, n))
            .OrderBy(n => n.Contains('/', StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int cap = source.Remote ? RemoteProbes : LocalProbes;
        var listed = new List<EventLogListing>();

        // One session for every log looked into (2026-10-20, a review: on a remote computer each Info signed in again).
        foreach (var (name, read, failure) in system.Infos(source.Target, names.Take(cap).ToList(), cancellationToken))
        {
            if (failure is { Code: 5 })
            {
                listed.Add(new EventLogListing(name, null, NoAccess: true));
                continue;
            }

            if (failure is not null || read is not { } info)
            {
                if (all && failure is not null)
                {
                    listed.Add(new EventLogListing(name, null, Error: EventLogErrors.Sentence(failure.Code, source.Context with { Log = name }, failure.Message)));
                }

                continue;
            }

            bool quiet = !info.Enabled || info.Records is 0 || info.Type is EventChannelType.Analytic or EventChannelType.Debug;
            if (all || !quiet)
            {
                listed.Add(new EventLogListing(name, info));
            }
        }

        return (listed, Math.Max(0, names.Count - cap));
    }

    /// <summary>How long the working directory's walk for <c>.evtx</c> files may take before the listing goes on without the rest.</summary>
    public static readonly TimeSpan FilesTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The working directory's <c>.evtx</c> files (<see cref="MaxFiles"/> at most, <see cref="FileDepth"/> folders down) with their sizes; never a throw.</summary>

    private async Task<(IReadOnlyList<EventLogFile> Files, bool Truncated)> FilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => EvtxFiles(Access.Files), CancellationToken.None).WaitAsync(FilesTimeout, Access.Time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ([], true);
        }
    }

    public static (IReadOnlyList<EventLogFile> Files, bool Truncated) EvtxFiles(WorkingDirectory files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var found = files.Find("*.evtx", "", MaxFiles, FileDepth);
        if (found.Outcome != FileOutcome.Ok)
        {
            return ([], false);
        }

        var list = new List<EventLogFile>(found.Paths.Count);
        foreach (string path in found.Paths)
        {
            long size = 0;
            if (files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok)
            {
                try
                {
                    size = new FileInfo(full).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }
            }

            list.Add(new EventLogFile(path, size));
        }

        return (list, found.Truncated);
    }
}
