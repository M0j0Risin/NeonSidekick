using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>session_manager(action, …)</c>: the model's window onto this profile's earlier sessions
/// (<see cref="SessionStore"/>, 2026-09-18) — <c>search</c> a query for the sessions whose turns match
/// it (each with its best turn's snippet), <c>list</c> the newest, <c>read</c> one by id for its turns
/// as <c>You:</c> / <c>Neon:</c> text. The conversation on screen is left out of a search and a list
/// (the model already has it). Never restores, renames or purges: those are the user's, through
/// <c>/sessions</c>. Every result starts with a header line, so the transcript's one-line note
/// (<see cref="Note"/>) reads <c>Searched "…" (3 sessions):</c>. The reads run off the caller's
/// thread (<see cref="SearchFilesTool"/>'s shape): the store is synchronous and the turn loop is the UI's.
/// </summary>
public sealed class SessionManagerTool : AIFunction
{
    public const string ToolName = "session_manager";
    public const string ActionArgument = "action";
    public const string QueryArgument = "query";
    public const string IdArgument = "id";
    public const string MaxResultsArgument = "max_results";
    public const string FromTurnArgument = "from_turn";
    public const string ToTurnArgument = "to_turn";

    public const string SearchAction = "search";
    public const string ListAction = "list";
    public const string ReadAction = "read";

    public const int MinResults = AppSettingsData.MinSessionSearchMaxResults;
    public const int MaxResults = AppSettingsData.MaxSessionSearchMaxResults;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["search", "list", "read"], "description": "search: the earlier sessions whose turns contain every word of query, best first, with a snippet each. list: the newest sessions. read: one session's turns by id." },
            "query": { "type": "string", "description": "For search: the words to look for (every one must occur)." },
            "id": { "type": "integer", "description": "For read: the session's number from a search or list result (#12 is 12)." },
            "max_results": { "type": "integer", "description": "For search and list: how many sessions, 1 to 20. Leave it out for the user's default." },
            "from_turn": { "type": "integer", "description": "For read: the first turn to show, 1-based. Leave it out for the start." },
            "to_turn": { "type": "integer", "description": "For read: the last turn to show, inclusive. Leave it out for the end." }
          },
          "required": ["action"]
        }
        """);

    private readonly SessionStore _store;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<long?> _current;
    private readonly TimeProvider _time;

    /// <param name="store">The profile's store.</param>
    /// <param name="effective">The settings in force at each call (<c>Session search max results</c>).</param>
    /// <param name="current">The session on screen, left out of a search and a list; null before its first turn.</param>
    /// <param name="time">The clock whose local zone the moments are shown in.</param>
    public SessionManagerTool(SessionStore store, Func<AppSettingsData> effective, Func<long?> current, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _time = time ?? TimeProvider.System;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Finds and reads the user's earlier conversations with you (this profile's stored sessions). " +
        "Use search with the words the user remembers, list for the newest ones, then read a session's turns by its id. " +
        "The conversation you are in is not included.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The count a call without <c>max_results</c> gets: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int DefaultCount(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.SessionSearchMaxResults, MinResults, MaxResults);
    }

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>The act itself, apart from the argument reading: the result text the model reads.</summary>
    public string Describe(string action, string query, long? id, int? maxResults, int? fromTurn, int? toTurn)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(query);
        var zone = _time.LocalTimeZone;
        switch (action.Trim().ToLowerInvariant())
        {
            case SearchAction:
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return SessionText.NoQuery;
                }

                if (Count(maxResults) is not { } count)
                {
                    return SessionText.BadResultCount(MinResults, MaxResults);
                }

                return SessionText.SearchResults(query.Trim(), _store.Search(query, count, _current()), zone);
            }

            case ListAction:
            {
                if (Count(maxResults) is not { } count)
                {
                    return SessionText.BadResultCount(MinResults, MaxResults);
                }

                long? current = _current();
                var sessions = _store.List(current is null ? count : count + 1).Where(s => s.Id != current).Take(count).ToList();
                return SessionText.ListResults(sessions, zone);
            }

            case ReadAction:
            {
                // Only search and list read max_results (#153).
                if (maxResults is not null)
                {
                    return ArgumentText.NotReadBy(MaxResultsArgument, ReadAction, SearchAction + " and " + ListAction);
                }

                if (id is not { } wanted)
                {
                    return SessionText.NoId;
                }

                return _store.Load(wanted) is { } record
                    ? SessionText.Read(record, fromTurn ?? 1, toTurn ?? 0, zone)
                    : SessionText.Missing(wanted);
            }

            default:
                return SessionText.BadAction(action);
        }
    }

    private int? Count(int? maxResults)
    {
        int count = maxResults ?? DefaultCount(_effective());
        return count < MinResults || count > MaxResults ? null : count;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, IdArgument, out var id, out var raw))
        {
            return ClockText.BadInteger(IdArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, MaxResultsArgument, out var max, out raw))
        {
            return ClockText.BadInteger(MaxResultsArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, FromTurnArgument, out var from, out raw))
        {
            return ClockText.BadInteger(FromTurnArgument, raw);
        }

        if (!ToolArguments.TryReadInt32(arguments, ToTurnArgument, out var to, out raw))
        {
            return ClockText.BadInteger(ToTurnArgument, raw);
        }

        string action = ToolArguments.ReadString(arguments, ActionArgument);
        string query = ToolArguments.ReadString(arguments, QueryArgument);
        // Off the caller's thread: the store is synchronous, and the turn loop is the UI's.
        return await Task.Run(() => Describe(action, query, id, max, from, to), cancellationToken).ConfigureAwait(false);
    }
}
