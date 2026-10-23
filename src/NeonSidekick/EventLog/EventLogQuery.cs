using System.Globalization;

namespace NeonSidekick.EventLog;

/// <summary>The levels a search keeps; <see cref="None"/> keeps every level.</summary>
[Flags]
public enum EventLevels
{
    None = 0,
    Critical = 1,
    Error = 2,
    Warning = 4,
    Information = 8,
    Verbose = 16,
}

/// <summary>Which audits a search keeps: the Security log's keywords.</summary>
public enum EventAudit
{
    Any,
    Success,
    Failure,
}

/// <summary>An id or a range of them (<c>4624</c>, <c>4700-4799</c>).</summary>
public readonly record struct EventIdRange(int From, int To)
{
    public bool Single => From == To;

    public bool Contains(int id) => id >= From && id <= To;

    public override string ToString() => Single ? From.ToString(CultureInfo.InvariantCulture) : From.ToString(CultureInfo.InvariantCulture) + "-" + To.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Where a page goes on from, and what <c>eventlog_event</c> reads (2026-10-20, a review: a forwarded log keeps each source computer's own
/// EventRecordID, so in ForwardedEvents a record number is neither unique nor in read order): a record alone (<c>81100</c>), or a record
/// with its <see cref="Source"/> tag (<c>1200@3f9a2c1b</c>), eight hex of a hash over the event's computer and channel — never their text,
/// which is the event's own and would sit outside a fence where the model copies it. A tagged cursor is never an XPath term: the search
/// reads from the top and skips to it (<see cref="EventLogSearch.Search"/>).
/// </summary>
/// <remarks>
/// <c>Time</c> (2026-10-23, the admin review's A5): a tagged cursor's event time, so the next page asks the log for events at or before it and
/// skips only from there; without it each page read from the newest again, and a budget stop handed back the same cursor, so deep pages never
/// advanced. Written as a third part, <c>N@tag@ticks</c>; an older two-part cursor still reads.
/// </remarks>
public readonly record struct EventCursor(long Record, string? Source = null, DateTimeOffset? Time = null)
{
    /// <summary>Whether the cursor names the event's source too (a forwarded log's).</summary>
    public bool Tagged => Source is not null;

    /// <summary>The cursor of <paramref name="system"/>'s event: tagged, or its record alone.</summary>
    public static EventCursor Of(EventSystemValues system, bool tagged)
    {
        ArgumentNullException.ThrowIfNull(system);
        return new EventCursor(system.Record, tagged ? Tag(system) : null, tagged ? system.Time : null);
    }

    /// <summary>The tag of an event's computer and channel (case ignored): the first four bytes of SHA-256 over both, in lowercase hex.</summary>
    public static string Tag(EventSystemValues system)
    {
        ArgumentNullException.ThrowIfNull(system);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(system.Computer.ToUpperInvariant() + "\n" + system.Channel.ToUpperInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    /// <summary>Whether <paramref name="system"/> is this cursor's event: its record, and its tag when the cursor has one.</summary>
    public bool Is(EventSystemValues system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.Record == Record && (Source is null || string.Equals(Tag(system), Source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="text"/> as a cursor: a record number from 1, alone or with <c>@</c> and an eight-hex tag; null for anything else.
    /// </summary>
    public static EventCursor? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim().TrimStart('#');
        int at = trimmed.IndexOf('@', StringComparison.Ordinal);
        string number = at < 0 ? trimmed : trimmed[..at];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long record) || record < 1)
        {
            return null;
        }

        if (at < 0)
        {
            return new EventCursor(record);
        }

        string tag = trimmed[(at + 1)..];
        DateTimeOffset? time = null;
        int second = tag.IndexOf('@', StringComparison.Ordinal);
        if (second >= 0)
        {
            if (!long.TryParse(tag[(second + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return null;
            }

            time = new DateTimeOffset(ticks, TimeSpan.Zero);
            tag = tag[..second];
        }

        return tag.Length == 8 && tag.All(char.IsAsciiHexDigit) ? new EventCursor(record, tag.ToLowerInvariant(), time) : null;
    }

    public override string ToString() =>
        Record.ToString(CultureInfo.InvariantCulture) + (Source is null ? "" : "@" + Source + (Time is { } time ? "@" + time.UtcTicks.ToString(CultureInfo.InvariantCulture) : ""));
}

/// <summary>A named field's value to match (<c>TargetUserName=alice</c>); EventData's names only.</summary>
public readonly record struct EventFieldTerm(string Name, string Value)
{
    public override string ToString() => Name + "=" + Value;
}

/// <summary>
/// One search (2026-10-15, the event-log plan): the tools' arguments and <c>/eventlog</c>'s words both land here, so the XPath, the
/// client-side match and the next page's restated call are worked out once. <see cref="Log"/> is the channel (an alias resolved) or the
/// <c>.evtx</c> path as given; <see cref="Since"/> and <see cref="Until"/> are absolute, taken from the clock when the call was read.
/// </summary>
public sealed record EventLogQuery
{
    /// <summary>The most events one page shows.</summary>
    public const int MaxLimit = 100;

    public required string Log { get; init; }

    public EventLevels Levels { get; init; }

    public IReadOnlyList<EventIdRange> Ids { get; init; } = [];

    public IReadOnlyList<string> Providers { get; init; } = [];

    public IReadOnlyList<EventFieldTerm> Fields { get; init; } = [];

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    public EventAudit Audit { get; init; }

    /// <summary>The page's start: only events below it (a tagged cursor: only those after it in read order).</summary>
    public EventCursor? BeforeRecord { get; init; }

    /// <summary>
    /// Whether <paramref name="log"/> is one whose events come from other computers and keep their own records (2026-10-20, a review):
    /// <c>ForwardedEvents</c>, the collector's default; its pages always carry tagged cursors.
    /// </summary>
    /// <remarks>An <c>.evtx</c> too (2026-10-23, the admin review's P1): a forwarded log's export keeps the sources' records.</remarks>
    public static bool IsForwarded(string? log) => string.Equals(log?.Trim(), "ForwardedEvents", StringComparison.OrdinalIgnoreCase) || (log is not null && IsFile(log));

    /// <summary>
    /// Why <paramref name="log"/> is no log's name, or null (2026-10-23, the admin review's K1: <c>Security\u0000/Operational</c> passed a
    /// <c>*/Operational</c> allow-list, and EvtQuery read to the NUL and opened Security): a control character anywhere.
    /// </summary>
    public static string? NameProblem(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return log.Any(char.IsControl) ? EventLogText.BadLogName(log) : null;
    }

    public string? Text { get; init; }

    public int Limit { get; init; } = 20;

    /// <summary>
    /// The view a log opens on when the call names no level or audit (2026-10-15): Security's audit failures (the Security log's events are
    /// all level 0; what matters there is the failed sign-in), every other log's critical events, errors and warnings.
    /// </summary>
    public static (EventLevels Levels, EventAudit Audit) DefaultFor(string channel) =>
        IsSecurity(channel) ? (EventLevels.None, EventAudit.Failure) : (EventLevels.Critical | EventLevels.Error | EventLevels.Warning, EventAudit.Any);

    /// <summary>Whether <paramref name="channel"/> is the Security log (case ignored).</summary>
    public static bool IsSecurity(string? channel) => string.Equals(channel?.Trim(), "Security", StringComparison.OrdinalIgnoreCase);

    /// <summary>The aliases a log's name may be given as, to the channel each names (case ignored). Pinned.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = "Application",
        ["sys"] = "System",
        ["sec"] = "Security",
        ["setup"] = "Setup",
        ["ps"] = "Microsoft-Windows-PowerShell/Operational",
    };

    /// <summary><paramref name="log"/> with an alias resolved (<see cref="Aliases"/>), trimmed; anything else as given.</summary>
    public static string ResolveAlias(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string trimmed = log.Trim();
        return Aliases.TryGetValue(trimmed, out string? channel) ? channel : trimmed;
    }

    /// <summary>Whether <paramref name="log"/> names an <c>.evtx</c> file rather than a channel.</summary>
    public static bool IsFile(string log) => log.Trim().EndsWith(".evtx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="text"/>'s levels — <c>critical</c>, <c>error</c>, <c>warning</c>, <c>information</c> (<c>info</c>), <c>verbose</c>,
    /// plurals read too, joined by commas or spaces; <c>all</c> or <c>any</c> for every level. The refusal when a word is none of those.
    /// </summary>
    public static (EventLevels Levels, string? Error) ParseLevels(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var levels = EventLevels.None;
        foreach (string raw in text.Split([',', ' ', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string word = raw.ToLowerInvariant();
            word = word.Length > 1 && word.EndsWith('s') && word != "errors" ? word[..^1] : word == "errors" ? "error" : word;
            switch (word)
            {
                case "all" or "any":
                    return (EventLevels.None, null);
                case "critical" or "crit":
                    levels |= EventLevels.Critical;
                    break;
                case "error" or "err":
                    levels |= EventLevels.Error;
                    break;
                case "warning" or "warn":
                    levels |= EventLevels.Warning;
                    break;
                case "information" or "info":
                    levels |= EventLevels.Information;
                    break;
                case "verbose":
                    levels |= EventLevels.Verbose;
                    break;
                default:
                    return (EventLevels.None, EventLogText.BadLevel(raw));
            }
        }

        return (levels, null);
    }

    /// <summary><paramref name="levels"/> as the words <see cref="ParseLevels"/> reads, joined by commas; <c>all</c> for none.</summary>
    public static string LevelWords(EventLevels levels)
    {
        if (levels == EventLevels.None)
        {
            return "all";
        }

        var words = new List<string>(5);
        if (levels.HasFlag(EventLevels.Critical))
        {
            words.Add("critical");
        }

        if (levels.HasFlag(EventLevels.Error))
        {
            words.Add("error");
        }

        if (levels.HasFlag(EventLevels.Warning))
        {
            words.Add("warning");
        }

        if (levels.HasFlag(EventLevels.Information))
        {
            words.Add("information");
        }

        if (levels.HasFlag(EventLevels.Verbose))
        {
            words.Add("verbose");
        }

        return string.Join(",", words);
    }

    /// <summary>Whether an event of <paramref name="level"/> is among <paramref name="levels"/> (information is 0 or 4, as Event Viewer reads it).</summary>
    public static bool LevelMatches(EventLevels levels, int level) => levels == EventLevels.None || level switch
    {
        1 => levels.HasFlag(EventLevels.Critical),
        2 => levels.HasFlag(EventLevels.Error),
        3 => levels.HasFlag(EventLevels.Warning),
        0 or 4 => levels.HasFlag(EventLevels.Information),
        5 => levels.HasFlag(EventLevels.Verbose),
        _ => false,
    };

    /// <summary><paramref name="text"/>'s ids: <c>4624</c>, <c>4700-4799</c>, joined by commas or spaces, each 0 to 65535. The refusal for anything else.</summary>
    public static (IReadOnlyList<EventIdRange> Ids, string? Error) ParseIds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = new List<EventIdRange>();
        foreach (string part in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-', 1);
            string from = dash < 0 ? part : part[..dash];
            string to = dash < 0 ? part : part[(dash + 1)..];
            if (!int.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out int a) || !int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out int b)
                || a > 65535 || b > 65535 || b < a)
            {
                return ([], EventLogText.BadIds(part));
            }

            ids.Add(new EventIdRange(a, b));
        }

        return (ids, null);
    }

    /// <summary><paramref name="text"/> as one <c>Name=value</c> term; the refusal for a missing <c>=</c> or a name that is not a field's.</summary>
    public static (EventFieldTerm? Term, string? Error) ParseField(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return (null, EventLogText.BadField(text));
        }

        string name = text[..equals].Trim();
        string value = text[(equals + 1)..].Trim();
        if (!IsFieldName(name))
        {
            return (null, EventLogText.BadField(text));
        }

        return (new EventFieldTerm(name, value), null);
    }

    /// <summary>Whether <paramref name="name"/> can be an EventData field's name: letters, digits, <c>_ . -</c>, 64 at most.</summary>
    public static bool IsFieldName(string name) =>
        name.Length is > 0 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary><paramref name="text"/> as an audit filter: <c>success</c>, <c>failure</c>, or <c>any</c> (<c>all</c>); the refusal for anything else.</summary>
    public static (EventAudit Audit, string? Error) ParseAudit(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Trim().ToLowerInvariant() switch
        {
            "success" or "successes" or "succeeded" => (EventAudit.Success, null),
            "failure" or "failures" or "failed" or "fail" => (EventAudit.Failure, null),
            "any" or "all" or "both" => (EventAudit.Any, null),
            _ => (EventAudit.Any, EventLogText.BadAudit(text.Trim())),
        };
    }
}
