using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.EventLog;

/// <summary>What one event-log call came to: its value, or the sentence that says why not (never both).</summary>
public readonly record struct EventLogResult<T>(T? Value, string? Error)
{
    public static EventLogResult<T> Ok(T value) => new(value, null);

    public static EventLogResult<T> Failed(string error) => new(default, error);
}

/// <summary>
/// Where one call reads (2026-10-15): the computer, the target the reader opens, the log (the channel, or the <c>.evtx</c> path as the
/// working directory names it), and whether the model reads it redacted.
/// </summary>
public sealed record EventLogSource(EventLogComputer Computer, EventLogTarget Target, string Log, bool Redacted)
{
    public bool IsFile => Target.Kind == EventLogTargetKind.File;

    public bool Remote => Target.Kind == EventLogTargetKind.Remote;

    /// <summary>Where the answer says it read: the computer's name, or this PC for a file.</summary>
    public string Where => IsFile ? EventLogText.ThisPc : Computer.Name;

    /// <summary>A failure's context, for <see cref="EventLogErrors.Sentence"/>.</summary>
    public EventLogErrorContext Context => new(Computer.IsLocal || IsFile ? EventLogText.ThisPc : Computer.Name, Log, Remote, Computer.Config.Host?.Trim(), Remote ? Computer.Account : null, IsFile);
}

/// <summary>
/// The event logs' one way in (2026-10-15, the event-log plan), the tools' and <c>/eventlog</c>'s alike: the computer a call means among the
/// ticked ones (<c>Event log computers offered</c>), the log (an alias resolved, the computer's <c>logs</c> checked; or an <c>.evtx</c> under
/// the working directory, which needs no tick and takes <c>local</c>'s detail), the sign-in (a <c>runas</c> password resolved per call), and
/// the read itself on a pool thread under a linked token — 30 s for this PC or a file, 60 s for a remote computer — with
/// <see cref="Task.WaitAsync(CancellationToken)"/>, so a native call that will not end is abandoned rather than waited out. A reader
/// failure is its sentence (<see cref="EventLogErrors"/>), never a throw; a cancel by the caller is the caller's.
/// </summary>
public sealed class EventLogAccess
{
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<EventLogCatalog> _loaded;
    private readonly Func<IReadOnlyList<string>?> _offered;

    /// <param name="system">The reader; null where there is none (off Windows): every call says so.</param>
    /// <param name="loaded">Every computer the two files hold, <c>local</c> first, read afresh at each call.</param>
    /// <param name="offered">The ticked names (<c>Event log computers offered</c>), read at each call.</param>
    /// <param name="files">The working directory an <c>.evtx</c> is found under.</param>
    /// <param name="time">The clock: the times a call names, the zone an answer shows, the scan's budget.</param>
    public EventLogAccess(IEventLogSystem? system, Func<EventLogCatalog> loaded, Func<IReadOnlyList<string>?> offered, WorkingDirectory files, TimeProvider time)
    {
        System = system;
        _loaded = loaded ?? throw new ArgumentNullException(nameof(loaded));
        _offered = offered ?? throw new ArgumentNullException(nameof(offered));
        Files = files ?? throw new ArgumentNullException(nameof(files));
        Time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public IEventLogSystem? System { get; }

    public WorkingDirectory Files { get; }

    public TimeProvider Time { get; }

    /// <summary>Every computer the files hold, ticked or not.</summary>
    public EventLogCatalog Loaded() => _loaded();

    /// <summary>The ticked computers.</summary>
    public EventLogCatalog Catalog() => _loaded().Offered(_offered());

    /// <summary>
    /// The computer <paramref name="computer"/> names among the ticked ones (or the default), with its target — the <c>runas</c> password
    /// resolved now. <paramref name="forceRedacted"/> (an advisor's, a script's call; a bot's until 2026-10-22) reads it redacted whatever its detail.
    /// </summary>
    public (EventLogSource? Source, string? Error) Computer(string? computer, bool forceRedacted)
    {
        var (found, error) = Catalog().Find(computer);
        if (found is null)
        {
            return (null, error);
        }

        if (found.IsLocal)
        {
            return (new EventLogSource(found, EventLogTarget.Local, "", forceRedacted || !found.Config.Full), null);
        }

        EventLogLogin? login = null;
        if (found.Config.IsRunAs)
        {
            var secret = EventLogSecrets.Resolve(found);
            if (secret.Error is { } missing)
            {
                return (null, EventLogText.CannotSignIn(found.Name, missing));
            }

            login = new EventLogLogin(found.Config.User!.Trim(), secret.Value!, found.Config.AuthMethodWord);
        }

        return (new EventLogSource(found, EventLogTarget.Remote(found.Config.Host!.Trim(), login), "", forceRedacted || !found.Config.Full), null);
    }

    /// <summary>
    /// Where a call reads: <paramref name="log"/> an <c>.evtx</c> under the working directory (refused with a <paramref name="computer"/>, or
    /// outside the sandbox, or missing), else a channel on the computer (an alias resolved before the computer's <c>logs</c> are checked).
    /// </summary>
    public (EventLogSource? Source, string? Error) Resolve(string? computer, string log, bool forceRedacted)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (EventLogQuery.IsFile(log))
        {
            if (!string.IsNullOrWhiteSpace(computer))
            {
                return (null, EventLogText.FileWithComputer);
            }

            var outcome = Files.Resolve(log.Trim(), forWrite: false, out string full);
            if (outcome != FileOutcome.Ok)
            {
                return (null, FileText.Error(outcome, log.Trim(), "read"));
            }

            if (!File.Exists(full))
            {
                return (null, EventLogText.FileMissing(log.Trim()));
            }

            // A file takes local's detail, ticked or not; no "logs" applies to a file (TOOLS.md names the gap).
            var local = Loaded().Computers.FirstOrDefault(c => c.IsLocal) ?? EventLogCatalog.LocalOnly.Computers[0];
            return (new EventLogSource(local, EventLogTarget.File(full), log.Trim(), forceRedacted || !local.Config.Full), null);
        }

        string channel = EventLogQuery.ResolveAlias(log);
        if (channel.Length == 0)
        {
            return (null, EventLogText.Missing("log"));
        }

        var (source, error) = Computer(computer, forceRedacted);
        if (source is null)
        {
            return (null, error);
        }

        if (!EventLogCatalog.Allows(source.Computer.Config.Logs, channel))
        {
            return (null, EventLogText.LogNotAllowed(channel, source.Computer.Name));
        }

        return (source with { Log = channel }, null);
    }

    /// <summary>The scan's bounds for <paramref name="source"/>: <paramref name="scanLimit"/> events, a quarter of it for a remote computer, and 10 s.</summary>
    public EventScan Scan(EventLogSource source, int scanLimit)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new EventScan(source.Redacted, source.Remote ? Math.Max(1, scanLimit / 4) : scanLimit, EventScan.DefaultBudget, Time);
    }

    /// <summary>
    /// Runs <paramref name="work"/> over the reader for <paramref name="source"/> on a pool thread, under a token that also fires at the
    /// timeout; the reader's failure as its sentence, the timeout as its own. <paramref name="cancellationToken"/>'s cancel is thrown.
    /// </summary>
    public async Task<EventLogResult<T>> RunAsync<T>(EventLogSource source, Func<IEventLogSystem, CancellationToken, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(work);
        if (System is not { } system)
        {
            return EventLogResult<T>.Failed(EventLogText.NoneHere);
        }

        using var timeout = new CancellationTokenSource(source.Remote ? RemoteTimeout : LocalTimeout, Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await Task.Run(() => work(system, token), CancellationToken.None).WaitAsync(token).ConfigureAwait(false);
            DiagnosticLog.Debug(EventLogText.Category, $"{Describe(source)} read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            return EventLogResult<T>.Ok(value);
        }
        catch (EventLogException ex)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{Describe(source)} failed: {ex.Message}");
            return EventLogResult<T>.Failed(EventLogErrors.Sentence(ex.Code, source.Context, ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Debug(EventLogText.Category, $"{Describe(source)} timed out");
            return EventLogResult<T>.Failed(EventLogText.TimedOut(source.Context.Computer));
        }
    }

    /// <summary>
    /// The channel a log's default view is read for (<see cref="EventLogQuery.DefaultFor"/>): the log itself, or the channel an <c>.evtx</c>
    /// holds, from its newest event (a Security export reads as Security). One helper for the tools, <c>/eventlog</c> and its pane
    /// (2026-10-20, a review: two copies); the reader renders only that event's system values.
    /// </summary>
    public static string ChannelOf(IEventLogSystem system, EventLogSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFile)
        {
            return source.Log;
        }

        using var reader = system.Query(source.Target, source.Log, "*", cancellationToken);
        return reader.Next() is { Count: > 0 } batch ? batch[0].System.Channel : "";
    }

    /// <summary>
    /// A computer tried as it stands (2026-10-15, the wizard's Test and <c>--eventlog-check</c>): its newest System event read under its
    /// account — the session, the sign-in and the read all proven — nothing written. Its sentence either way.
    /// </summary>
    public static async Task<(bool Ok, string Text)> TestAsync(IEventLogSystem? system, EventLogComputer computer, WorkingDirectory files, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var access = new EventLogAccess(system, () => new EventLogCatalog([computer], []), () => [computer.Name], files, time);
        var (source, error) = access.Resolve(computer.Name, "System", forceRedacted: true);
        if (source is null)
        {
            return (false, error!);
        }

        var result = await access.RunAsync(source, (reader, token) =>
        {
            using var query = reader.Query(source.Target, source.Log, "*", token);
            return query.Next() is { Count: > 0 } batch ? batch[0].System : null;
        }, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return (false, failed);
        }

        return (true, result.Value is { } newest
            ? EventLogText.TestOk(computer.Name, newest.Record, EventLogText.Time(newest.Time, time.LocalTimeZone))
            : EventLogText.TestEmpty(computer.Name));
    }

    /// <summary>
    /// The log line of one query (2026-10-15, the plan's rule): the target, the log, the XPath with every literal replaced by its length,
    /// and <c>text</c> only as a length — a password never lands in the log.
    /// </summary>
    public static void LogQuery(EventLogSource source, string xpath, string? text, int found, int scanned)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(xpath);
        DiagnosticLog.Debug(EventLogText.Category,
            $"{Describe(source)} {EventLogXPath.ForLog(xpath)}" + (string.IsNullOrEmpty(text) ? "" : $" text({text.Length.ToString(CultureInfo.InvariantCulture)})")
            + $" → {found.ToString(CultureInfo.InvariantCulture)} of {scanned.ToString(CultureInfo.InvariantCulture)} scanned");
    }

    private static string Describe(EventLogSource source) =>
        source.IsFile ? "file " + source.Log : (source.Remote ? source.Computer.Name + " (" + source.Target.Host + ")" : EventLogText.LocalName) + (source.Log.Length > 0 ? " " + source.Log : "");
}
