namespace NeonSidekick.EventLog;

/// <summary>
/// An event as an answer shows it (2026-10-15): its system values, its message (redacted under that detail; a forwarded event's own
/// text when its provider is not here), why it has none, its fields (redacted likewise), the classic source's name, and — the full
/// detail's event page alone — its XML.
/// </summary>
public sealed record EventView(EventSystemValues System, string? Message, EventMessageProblem Problem, IReadOnlyList<EventField> Fields, string? SourceName = null, string? Xml = null)
{
    /// <summary>Whether the event carries <see cref="EventLogText.UnregisteredSource"/>: no publisher here, or no message for it.</summary>
    public bool Unregistered => Problem is EventMessageProblem.Unregistered or EventMessageProblem.NoMessage;

    /// <summary>The message's first non-empty line, or null.</summary>
    public string? FirstLine => Message?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}

/// <summary>
/// Whether an event is one a search keeps (2026-10-15, the event-log plan), checked here whatever the server already filtered — so the
/// fake reader needs no XPath, and a server's looser reading never widens an answer: the levels, ids, providers, times, audit and
/// cursor from the system values; then the field terms and <c>text</c>, case ignored. Under <c>redacted</c> both match the
/// <b>redacted</b> values (no raw server-side term can probe a guessed secret), and the message is formatted last, only when the
/// cheaper parts did not already decide. Pure.
/// </summary>
public static class EventLogMatch
{
    /// <summary>Whether <paramref name="system"/> passes the query's system terms.</summary>
    public static bool System(EventSystemValues system, EventLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(query);
        if (!EventLogQuery.LevelMatches(query.Levels, system.Level))
        {
            return false;
        }

        if (query.Ids.Count > 0 && !query.Ids.Any(r => r.Contains(system.Id)))
        {
            return false;
        }

        if (query.Providers.Count > 0 && !query.Providers.Any(p => string.Equals(p, system.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (query.Since is { } since && (system.Time is not { } t1 || t1 < since))
        {
            return false;
        }

        if (query.Until is { } until && (system.Time is not { } t2 || t2 > until))
        {
            return false;
        }

        if (query.Audit == EventAudit.Success && !system.AuditSuccess || query.Audit == EventAudit.Failure && !system.AuditFailure)
        {
            return false;
        }

        // A tagged cursor is the search's skip, not a comparison: a forwarded log's records are not in read order.
        return query.BeforeRecord is not { Tagged: false } before || system.Record < before.Record;
    }

    /// <summary>Whether <paramref name="fields"/> hold every field term of the query (name and value, case ignored).</summary>
    public static bool Fields(IReadOnlyList<EventField> fields, EventLogQuery query)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(query);
        return query.Fields.All(term => fields.Any(f =>
            string.Equals(f.Name, term.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Value.Trim(), term.Value, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Whether <paramref name="text"/> is in the provider, a field's value or (asked last, formatting it only then) the message; null
    /// or empty matches everything.
    /// </summary>
    public static bool Text(string? text, EventSystemValues system, IReadOnlyList<EventField> fields, Func<string?> message)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (system.Provider.Contains(text, StringComparison.OrdinalIgnoreCase) || fields.Any(f => f.Value.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return message() is { } m && m.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}
