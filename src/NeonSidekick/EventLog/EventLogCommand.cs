using System.Globalization;
using System.Text;
using NeonSidekick.UI;

namespace NeonSidekick.EventLog;

/// <summary>
/// What a <c>/eventlog</c> line asks for (2026-10-15): the log (an alias, a channel, a quoted name with spaces, or an <c>.evtx</c>), the
/// computer, and the query's terms; <see cref="LevelGiven"/> and <see cref="AuditGiven"/> say whether the log's default view applies.
/// </summary>
/// <remarks>
/// <c>SinceText</c>, <c>UntilText</c> (2026-10-23, the admin review's H12): the times as written, so a refresh reads them again from now
/// (<see cref="At"/>); <c>since:1h</c> was fixed at the line's parse and never moved.
/// </remarks>
public sealed record EventLogCommandArgs(string Log, string? Computer, EventLogQuery Query, bool LevelGiven, bool AuditGiven, bool LogGiven, string? SinceText = null, string? UntilText = null)
{
    /// <summary>The line with its written times read again at <paramref name="now"/> (H12); itself when it wrote none.</summary>
    public EventLogCommandArgs At(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var query = Query;
        if (SinceText is { } since && Llm.Tools.TimeArgument.Parse(since, now, zone) is { } s)
        {
            query = query with { Since = s };
        }

        if (UntilText is { } until && Llm.Tools.TimeArgument.Parse(until, now, zone) is { } u)
        {
            query = query with { Until = u };
        }

        return this with { Query = query };
    }

    /// <summary>The query for <paramref name="channel"/>: the line's terms, the channel's default view where the line named neither a level nor an audit.</summary>
    public EventLogQuery For(string log, string channel)
    {
        var query = Query with { Log = log };
        if (LevelGiven || AuditGiven)
        {
            return query;
        }

        var (levels, audit) = EventLogQuery.DefaultFor(channel);
        return query with { Levels = levels, Audit = audit };
    }
}

/// <summary>What one <c>/eventlog</c> line printed came to: the lines, and whether it failed.</summary>
public sealed record EventLogCommandResult(IReadOnlyList<string> Lines, bool Failed)
{
    public static EventLogCommandResult Error(string line) => new([line], true);
}

/// <summary>
/// <c>/eventlog</c>'s words (2026-10-15, the event-log plan's phase 2): <c>/eventlog &lt;log|"name with spaces"|file.evtx&gt;
/// [computer:dc01] [level:error] [id:41] [provider:X] [field:Name=v] [since:2h] [until:…] [audit:failure] [text…]</c>, every term landing in
/// an <see cref="EventLogQuery"/> as the tools' arguments do. The user's own hand: it works with <c>Event log tools</c> off, reaches only the
/// ticked computers (an <c>.evtx</c> needs no tick), and shows full text whatever a computer's detail. The screen shows it on a pane
/// (<c>App/EventLogMenu</c>); headless, the Web UI and the screen without a pane print <see cref="RunAsync"/>'s lines.
/// </summary>
public static class EventLogCommand
{
    public const string Word = "/eventlog";

    /// <summary>The log a line with none opens on.</summary>
    public const string DefaultLog = "System";

    /// <summary>The term keys, in completion's order. Pinned.</summary>
    public static readonly IReadOnlyList<string> Keys = ["computer:", "level:", "id:", "provider:", "field:", "since:", "until:", "audit:"];

    /// <summary>The four logs the pane's tabs offer, in order. Pinned.</summary>
    public static readonly IReadOnlyList<string> TabLogs = ["Application", "System", "Security", "Setup"];

    /// <summary>The words split as a person types them: spaces part words, double quotes keep a word's spaces (<c>"Directory Service"</c>, <c>provider:"Service Control Manager"</c>).</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = new List<string>();
        var word = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }

                continue;
            }

            word.Append(c);
            any = true;
        }

        if (any)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary><paramref name="args"/> read into a query (times from <paramref name="now"/> in <paramref name="zone"/>), or the first refusal.</summary>
    public static (EventLogCommandArgs? Args, string? Error) Parse(string args, DateTimeOffset now, TimeZoneInfo zone, int limit)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(zone);
        string? log = null, computer = null, sinceText = null, untilText = null;
        var query = new EventLogQuery { Log = "", Limit = limit };
        bool levelGiven = false, auditGiven = false;
        var text = new List<string>();
        var providers = new List<string>();
        var fields = new List<EventFieldTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool first = true;
        foreach (string word in Words(args))
        {
            int colon = word.IndexOf(':', StringComparison.Ordinal);
            string key = colon > 0 ? word[..(colon + 1)].ToLowerInvariant() : "";
            string value = colon > 0 ? word[(colon + 1)..].Trim() : "";
            bool firstWord = first;
            first = false;
            if (!Keys.Contains(key))
            {
                // The log only as the line's first word (2026-10-23, the admin review's W6: the first word that was no key, anywhere, became
                // the log, so /eventlog level:error disk failure read a log named "disk").
                if (firstWord)
                {
                    log = word;
                }
                else
                {
                    text.Add(word);
                }

                continue;
            }

            if (value.Length == 0)
            {
                // A key with no value is said, never read as no filter (W10: level: showed every audit success, computer: read this PC, and
                // computer: dc01 with a space read the log "dc01").
                return (null, EventLogText.KeyNeedsValue(key));
            }

            if (key is "computer:" or "level:" or "audit:" or "since:" or "until:" && !seen.Add(key))
            {
                // One of each (P16: a second kept the last silently).
                return (null, EventLogText.KeyTwice(key));
            }

            switch (key)
            {
                case "computer:":
                    computer = value;
                    break;
                case "level:":
                {
                    var (levels, error) = EventLogQuery.ParseLevels(value);
                    if (error is not null)
                    {
                        return (null, error);
                    }

                    query = query with { Levels = levels };
                    levelGiven = true;
                    break;
                }

                case "id:":
                {
                    var (ids, error) = EventLogQuery.ParseIds(value);
                    if (error is not null)
                    {
                        return (null, error);
                    }

                    query = query with { Ids = [.. query.Ids, .. ids] };
                    break;
                }

                case "provider:":
                    providers.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "field:":
                {
                    var (term, error) = EventLogQuery.ParseField(value);
                    if (term is null)
                    {
                        return (null, error);
                    }

                    fields.Add(term.Value);
                    break;
                }

                case "since:" or "until:":
                {
                    if (Llm.Tools.TimeArgument.Parse(value, now, zone) is not { } moment)
                    {
                        return (null, EventLogText.BadTime(key.TrimEnd(':'), value));
                    }

                    query = key == "since:" ? query with { Since = moment } : query with { Until = moment };
                    if (key == "since:")
                    {
                        sinceText = value;
                    }
                    else
                    {
                        untilText = value;
                    }

                    break;
                }

                case "audit:":
                {
                    var (audit, error) = EventLogQuery.ParseAudit(value);
                    if (error is not null)
                    {
                        return (null, error);
                    }

                    query = query with { Audit = audit };
                    auditGiven = true;
                    break;
                }
            }
        }

        query = query with { Providers = providers, Fields = fields, Text = text.Count == 0 ? null : string.Join(" ", text) };
        string chosen = log ?? DefaultLog;
        return (new EventLogCommandArgs(chosen, string.IsNullOrWhiteSpace(computer) ? null : computer, query with { Log = chosen }, levelGiven, auditGiven, log is not null, sinceText, untilText), null);
    }

    /// <summary>
    /// <c>/eventlog</c>'s argument list: the logs and the working directory's <c>.evtx</c> files (quoted when they hold a space) and the term
    /// keys; after <c>computer:</c> the ticked computers, after <c>level:</c> and <c>audit:</c> their words. Never reads a log.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> computers, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(computers);
        ArgumentNullException.ThrowIfNull(files);
        int start = argText.LastIndexOf(' ') + 1;
        string head = argText[..start];
        string last = argText[start..];
        IEnumerable<CompletionItem> pool;
        if (last.StartsWith("computer:", StringComparison.OrdinalIgnoreCase))
        {
            pool = computers.Select(c => new CompletionItem("computer:" + c, c == EventLogText.LocalName ? EventLogText.ThisPc : "a ticked computer"));
        }
        else if (last.StartsWith("level:", StringComparison.OrdinalIgnoreCase))
        {
            pool = new[] { "critical", "error", "warning", "information", "verbose", "all" }.Select(l => new CompletionItem("level:" + l, "the level"));
        }
        else if (last.StartsWith("audit:", StringComparison.OrdinalIgnoreCase))
        {
            pool = new[] { "failure", "success", "any" }.Select(a => new CompletionItem("audit:" + a, "the Security log's audits"));
        }
        else
        {
            var items = new List<CompletionItem>();
            if (head.Trim().Length == 0)
            {
                items.AddRange(TabLogs.Select(l => new CompletionItem(l, "a log")));
                items.AddRange(EventLogQuery.Aliases.Select(a => new CompletionItem(a.Key, a.Value)));
                items.AddRange(files.Select(f => new CompletionItem(f.Contains(' ', StringComparison.Ordinal) ? "\"" + f + "\"" : f, "an .evtx file")));
            }

            items.AddRange(Keys.Select(k => new CompletionItem(k, KeyNote(k))));
            pool = items;
        }

        return MentionCompleter.Matches(pool.Select(i => i with { Text = head + i.Text }).ToList(), argText);
    }

    private static string KeyNote(string key) => key switch
    {
        "computer:" => "a ticked computer",
        "level:" => "critical, error, warning, information, verbose, all",
        "id:" => "event ids and ranges: 41,1074 or 4700-4799",
        "provider:" => "the provider (source)",
        "field:" => "a named value: Name=value",
        "since:" => "an age (2h, 1d) or a moment",
        "until:" => "an age or a moment",
        _ => "failure, success or any",
    };

    /// <summary>
    /// Runs one <c>/eventlog</c> line as lines (headless, the Web UI, the screen without a pane): the header, then a row per event. The
    /// user's own hand: full text, the ticked computers, the switch not asked.
    /// </summary>
    public static async Task<EventLogCommandResult> RunAsync(EventLogAccess access, string args, int limit, int scanLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(args);
        if (!OperatingSystem.IsWindows() || access.System is null)
        {
            return EventLogCommandResult.Error(EventLogText.NoneHere);
        }

        var now = access.Time.GetUtcNow();
        var zone = access.Time.LocalTimeZone;
        var (parsed, error) = Parse(args, now, zone, limit);
        if (parsed is null)
        {
            return EventLogCommandResult.Error(error!);
        }

        var (source, refused) = access.Resolve(parsed.Computer, parsed.Log, forceRedacted: false);
        if (source is null)
        {
            return EventLogCommandResult.Error(refused!);
        }

        source = source with { Redacted = false };
        var scan = access.Scan(source, scanLimit);
        var result = await access.RunAsync(source, (system, token) =>
        {
            string channel = EventLogAccess.ChannelOf(system, source, token);
            var query = parsed.For(source.Log, channel);
            var (xpath, built) = EventLogXPath.Build(query, serverFields: false);   // the client's match alone (W2)
            if (xpath is null)
            {
                return (Page: (EventSearchPage?)null, Query: query, Error: built);
            }

            using var reader = system.Query(source.Target, source.Log, xpath, token);
            var page = EventLogSearch.Search(reader, query, scan, token);
            EventLogAccess.LogQuery(source, xpath, query.Text, page.Events.Count, page.Scanned);
            return (Page: page, Query: query, Error: (string?)null);
        }, cancellationToken).ConfigureAwait(false);
        if (result.Error is { } failed)
        {
            return EventLogCommandResult.Error(failed);
        }

        var (found, used, problem) = result.Value;
        if (found is null)
        {
            return EventLogCommandResult.Error(problem!);
        }

        var lines = new List<string> { Header(source, used, found, now, zone) };
        lines.AddRange(found.Events.Select(e => PlainLine(e, zone)));
        if (found.BudgetStop)
        {
            lines.Add(EventLogRender.BudgetNote(found.Scanned, found.ReachedTime, zone));
        }

        return new EventLogCommandResult(lines, false);
    }

    /// <summary>The lines' header: <c>System on local: 20 events, newest first</c>, or the empty view's sentence.</summary>
    public static string Header(EventLogSource source, EventLogQuery query, EventSearchPage page, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        string where = EventLogText.Scrub(source.Log) + " on " + source.Where;
        return page.Events.Count == 0
            ? EventLogText.NoEvents(EventLogText.Scrub(source.Log), query.Levels, query.Audit, query.Since, now, zone) + EventLogText.Narrowed(query) + (source.IsFile ? "" : " on " + source.Where)
            : where + ": " + EventLogText.Count(page.Events.Count, "event") + ", newest first";
    }

    /// <summary>One event as a printed line: <c>#81234  2026-10-14 21:03:11 -05:00  error  Service Control Manager  7000  The … service failed</c>, scrubbed.</summary>
    public static string PlainLine(EventView view, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(view);
        var s = view.System;
        string message = (view.FirstLine ?? EventLogRender.NoMessage(view)).Replace('\t', ' ');
        return EventLogText.Scrub("#" + s.Record.ToString(CultureInfo.InvariantCulture) + "  " + EventLogText.Time(s.Time, zone) + "  " + EventLogText.Level(s.Level, s.Keywords)
            + "  " + s.Provider + "  " + s.Id.ToString(CultureInfo.InvariantCulture) + "  " + message);
    }
}
