using Microsoft.Data.Sqlite;

namespace NeonSidekick.Sessions;

/// <summary>
/// One model's figures over the saved turns (2026-11-21, feature idea #7, <c>/usage</c> › Models): <paramref name="Errors"/> are the
/// calls that answered with an error, each turn's capped at its calls (a failed bot turn with no calls stores 1, which would read past
/// 100%); <paramref name="TimedOutput"/> over <paramref name="OutputMs"/> is the speed, from the turns that have both;
/// <paramref name="Untagged"/> counts the turns from before the per-turn column, counted under their session's model.
/// </summary>
public sealed record ModelStat(string Model, int Turns, int Sessions, long ToolCalls, long Errors, int Cancelled, long Output, long TimedOutput, long OutputMs,
    DateTimeOffset LastUsed, int Untagged)
{
    /// <summary>Tool calls a turn.</summary>
    public double CallsPerTurn => Turns == 0 ? 0 : (double)ToolCalls / Turns;

    /// <summary>The share of tool calls that answered with an error; null with no calls.</summary>
    public double? ErrorRate => ToolCalls == 0 ? null : (double)Errors / ToolCalls;

    /// <summary>The share of turns the keys cut short.</summary>
    public double CancelledRate => Turns == 0 ? 0 : (double)Cancelled / Turns;

    /// <summary>Mean output tokens a turn.</summary>
    public double OutputPerTurn => Turns == 0 ? 0 : (double)Output / Turns;

    /// <summary>Tokens a second over the timed turns, as <see cref="Llm.TokenUsage.TokensPerSecond"/> reads one request; null with none timed.</summary>
    public double? TokensPerSecond => OutputMs > 0 && TimedOutput > 0 ? TimedOutput / (OutputMs / 1000.0) : null;
}

public sealed partial class SessionStore
{
    /// <summary>
    /// The saved turns grouped by the model that answered (2026-11-21, feature idea #7), newest-used first: a turn's own model, else its
    /// session's (a turn from before the column). One grouped query; while the added columns are missing for this run, by the session's
    /// model alone with no speed. Empty while the store is unavailable.
    /// </summary>
    public IReadOnlyList<ModelStat> ModelStats()
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return [];
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = _extraColumns
                    ? """
                      SELECT COALESCE(NULLIF(t.model, ''), s.model) AS m, count(*), count(DISTINCT t.session_id), sum(t.tool_calls), sum(min(t.errors, t.tool_calls)),
                             sum(t.cancelled), sum(t.output_tokens),
                             sum(CASE WHEN t.output_ms > 0 AND t.timed_output > 0 THEN t.timed_output ELSE 0 END),
                             sum(CASE WHEN t.output_ms > 0 AND t.timed_output > 0 THEN t.output_ms ELSE 0 END),
                             max(t.at), sum(CASE WHEN t.model IS NULL OR t.model = '' THEN 1 ELSE 0 END)
                      FROM turns t JOIN sessions s ON s.id = t.session_id GROUP BY m ORDER BY max(t.at) DESC, m
                      """
                    : """
                      SELECT s.model AS m, count(*), count(DISTINCT t.session_id), sum(t.tool_calls), sum(min(t.errors, t.tool_calls)),
                             sum(t.cancelled), sum(t.output_tokens), 0, 0, max(t.at), count(*)
                      FROM turns t JOIN sessions s ON s.id = t.session_id GROUP BY m ORDER BY max(t.at) DESC, m
                      """;
                using var reader = command.ExecuteReader();
                var stats = new List<ModelStat>();
                while (reader.Read())
                {
                    stats.Add(new ModelStat(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
                        reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), Parse(reader.GetString(9)), reader.GetInt32(10)));
                }

                return stats;
            }
            catch (SqliteException ex)
            {
                Fail("read the model figures", ex);
                return [];
            }
        }
    }
}
