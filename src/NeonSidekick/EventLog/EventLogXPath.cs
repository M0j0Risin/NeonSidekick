using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.EventLog;

/// <summary>
/// A search's XPath, as the event log takes it (2026-10-15, the event-log plan): the providers, the levels (information is
/// <c>Level=0 or Level=4</c>), the ids and ranges, <b>absolute</b> times from the injected clock (<c>TimeCreated[@SystemTime&gt;=…Z]</c>,
/// never <c>timediff</c>, so a page's next call means the same moment), the Security log's audit keywords (<c>band(Keywords,…)</c>), the
/// cursor (<c>EventRecordID&lt;N</c>) and, when the server may see them, EventData's named values (<c>UserData</c>'s are reached by
/// <c>text</c>). A literal is quoted with <c>'</c> or <c>"</c>, whichever it does not hold; one holding both, a control character, or
/// more than <see cref="MaxLiteral"/> characters is refused.
///
/// <para><b>The budget.</b> The event log refuses (15001) an expression nested past about 23 terms — a run of <c>or</c>s or
/// <c>and</c>s counts each, wherever it sits (the spike, 2026-10-15) — so the whole expression is held to <see cref="MaxTerms"/>: every
/// level, id (a range's two ends), provider, field, time bound, audit and cursor counts one. Checked before building, refused with a
/// sentence that says to narrow it.</para>
/// </summary>
public static partial class EventLogXPath
{
    /// <summary>The most terms one search's expression may hold.</summary>
    public const int MaxTerms = 20;

    /// <summary>The longest literal (a provider's name, a field's value).</summary>
    public const int MaxLiteral = 256;

    /// <summary>How many terms <paramref name="query"/> puts in the expression; <paramref name="serverFields"/> counts the field terms.</summary>
    public static int Terms(EventLogQuery query, bool serverFields)
    {
        ArgumentNullException.ThrowIfNull(query);
        int terms = LevelValues(query.Levels).Count + query.Ids.Sum(r => r.Single ? 1 : 2) + query.Providers.Count;
        terms += (query.Since is null ? 0 : 1) + (query.Until is null ? 0 : 1) + (query.Audit == EventAudit.Any ? 0 : 1) + (query.BeforeRecord is null ? 0 : 1);
        return terms + (serverFields ? query.Fields.Count : 0);
    }

    /// <summary>
    /// The expression for <paramref name="query"/>, or the refusal: over the budget, or a literal that cannot be quoted. With
    /// <paramref name="serverFields"/> false (the redacted detail) the field terms are left for the client-side match.
    /// </summary>
    public static (string? XPath, string? Error) Build(EventLogQuery query, bool serverFields)
    {
        ArgumentNullException.ThrowIfNull(query);
        int terms = Terms(query, serverFields);
        if (terms > MaxTerms)
        {
            return (null, EventLogText.TooManyTerms(terms, MaxTerms));
        }

        var system = new List<string>();
        var levels = LevelValues(query.Levels);
        if (levels.Count > 0)
        {
            system.Add(Group(levels.Select(l => "Level=" + l.ToString(CultureInfo.InvariantCulture))));
        }

        if (query.Ids.Count > 0)
        {
            system.Add(Group(query.Ids.Select(r => r.Single
                ? "EventID=" + r.From.ToString(CultureInfo.InvariantCulture)
                : "(EventID>=" + r.From.ToString(CultureInfo.InvariantCulture) + " and EventID<=" + r.To.ToString(CultureInfo.InvariantCulture) + ")")));
        }

        if (query.Providers.Count > 0)
        {
            var names = new List<string>(query.Providers.Count);
            foreach (string provider in query.Providers)
            {
                if (Quote(provider) is not { } quoted)
                {
                    return (null, EventLogText.BadLiteral(provider));
                }

                names.Add("@Name=" + quoted);
            }

            system.Add("Provider[" + string.Join(" or ", names) + "]");
        }

        if (query.Since is { } since)
        {
            system.Add("TimeCreated[@SystemTime>='" + Moment(since) + "']");
        }

        if (query.Until is { } until)
        {
            system.Add("TimeCreated[@SystemTime<='" + Moment(until) + "']");
        }

        if (query.Audit != EventAudit.Any)
        {
            ulong keyword = query.Audit == EventAudit.Success ? EventSystemValues.AuditSuccessKeyword : EventSystemValues.AuditFailureKeyword;
            system.Add("band(Keywords," + keyword.ToString(CultureInfo.InvariantCulture) + ")");
        }

        if (query.BeforeRecord is { } before)
        {
            system.Add("EventRecordID<" + before.ToString(CultureInfo.InvariantCulture));
        }

        var parts = new List<string>(2);
        if (system.Count > 0)
        {
            parts.Add("System[" + string.Join(" and ", system) + "]");
        }

        if (serverFields && query.Fields.Count > 0)
        {
            var data = new List<string>(query.Fields.Count);
            foreach (var field in query.Fields)
            {
                if (!EventLogQuery.IsFieldName(field.Name))
                {
                    return (null, EventLogText.BadField(field.ToString()));
                }

                if (Quote(field.Value) is not { } quoted)
                {
                    return (null, EventLogText.BadLiteral(field.Value));
                }

                data.Add("Data[@Name='" + field.Name + "']=" + quoted);
            }

            parts.Add("EventData[" + string.Join(" and ", data) + "]");
        }

        return (parts.Count == 0 ? "*" : "*[" + string.Join(" and ", parts) + "]", null);
    }

    /// <summary>The level values <paramref name="levels"/> stands for, in order (information is 0 and 4).</summary>
    public static IReadOnlyList<int> LevelValues(EventLevels levels)
    {
        var values = new List<int>(6);
        if (levels.HasFlag(EventLevels.Critical))
        {
            values.Add(1);
        }

        if (levels.HasFlag(EventLevels.Error))
        {
            values.Add(2);
        }

        if (levels.HasFlag(EventLevels.Warning))
        {
            values.Add(3);
        }

        if (levels.HasFlag(EventLevels.Information))
        {
            values.Add(0);
            values.Add(4);
        }

        if (levels.HasFlag(EventLevels.Verbose))
        {
            values.Add(5);
        }

        return values;
    }

    /// <summary><paramref name="text"/> as an XPath literal: in <c>'</c>, else in <c>"</c>; null when it holds both, a control character, or is too long.</summary>
    public static string? Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLiteral || text.Any(char.IsControl))
        {
            return null;
        }

        if (!text.Contains('\'', StringComparison.Ordinal))
        {
            return "'" + text + "'";
        }

        return text.Contains('"', StringComparison.Ordinal) ? null : "\"" + text + "\"";
    }

    /// <summary>A moment as the event log's <c>@SystemTime</c> compares it: UTC, milliseconds, <c>Z</c>.</summary>
    public static string Moment(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="xpath"/> for the log (2026-10-15, the plan's logging rule): every quoted literal replaced by its length,
    /// <c>'…'(7)</c>, so a provider's name or a field's value — which may be a secret — never lands in the log.
    /// </summary>
    public static string ForLog(string xpath)
    {
        ArgumentNullException.ThrowIfNull(xpath);
        return Literal().Replace(xpath, m => "'…'(" + (m.Length - 2).ToString(CultureInfo.InvariantCulture) + ")");
    }

    private static string Group(IEnumerable<string> terms)
    {
        var list = terms.ToList();
        return list.Count == 1 ? list[0] : "(" + string.Join(" or ", list) + ")";
    }

    [GeneratedRegex("'[^']*'|\"[^\"]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
}
