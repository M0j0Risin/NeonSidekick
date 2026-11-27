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

/// <summary>
/// One model's bests over the saved turns (2026-11-27, <c>/usage</c>'s Records): the fastest reply's speed, the longest answer, the slowest
/// first token with that turn's context, and the output summed. Null where no turn had the figure.
/// </summary>
public sealed record ModelBest(string Model, double? Speed, long? LongestOutput, long? SlowestStartMs, long? SlowestStartContext, long Written);

/// <summary>
/// The Records' all-time columns (2026-11-27): <see cref="ThisModel"/>'s bests, and each best over every model with the model holding it.
/// Empty (<see cref="None"/>) without the per-reply columns or while the store is unavailable, so the all-time columns don't show.
/// </summary>
public sealed record ModelBests(ModelBest? ThisModel, (double Value, string Model)? Speed, (long Value, string Model)? LongestOutput,
    (long Ms, long? Context, string Model)? SlowestStart, long Written)
{
    public static ModelBests None { get; } = new(null, null, null, null, 0);

    /// <summary>Whether there is anything to show.</summary>
    public bool Any => ThisModel is not null || Speed is not null || LongestOutput is not null || SlowestStart is not null;

    /// <summary>
    /// The bests from each model's (<see cref="SessionStore.ModelBestsByModel"/>): the turns a command answered (<c>/claude</c>, <c>/advisor</c>,
    /// <c>/cmd</c>) hold no record, as the In use table leaves them out; <paramref name="model"/> matched ignoring case. Pure.
    /// </summary>
    public static ModelBests Of(IReadOnlyList<ModelBest> byModel, string? model)
    {
        ArgumentNullException.ThrowIfNull(byModel);
        var real = byModel.Where(b => !SessionText.IsSideTurnModel(b.Model)).ToList();
        if (real.Count == 0)
        {
            return None;
        }

        var mine = model is null ? null : real.FirstOrDefault(b => string.Equals(b.Model, model, StringComparison.OrdinalIgnoreCase));
        var speed = real.Where(b => b.Speed is not null).MaxBy(b => b.Speed);
        var longest = real.Where(b => b.LongestOutput is not null).MaxBy(b => b.LongestOutput);
        var slowest = real.Where(b => b.SlowestStartMs is not null).MaxBy(b => b.SlowestStartMs);
        return new ModelBests(mine,
            speed is null ? null : (speed.Speed!.Value, speed.Model),
            longest is null ? null : (longest.LongestOutput!.Value, longest.Model),
            slowest is null ? null : (slowest.SlowestStartMs!.Value, slowest.SlowestStartContext, slowest.Model),
            real.Sum(b => b.Written));
    }
}

public sealed partial class SessionStore
{
    /// <summary>
    /// <see cref="ModelBestsByModel"/> folded for <paramref name="model"/> (2026-11-27, <c>/usage</c>'s Records): its bests, and every model's.
    /// </summary>
    public ModelBests ModelBests(string? model) => Sessions.ModelBests.Of(ModelBestsByModel(), model);

    /// <summary>
    /// Each model's bests over the saved turns (2026-11-27), grouped by <see cref="ModelStats"/>' model (a turn's own, else its session's): two
    /// statements in one command, the second the slowest start's own context through <c>ROW_NUMBER()</c>, since SQLite takes a bare column from
    /// the max row only when a query has one <c>max()</c>. A speed past <see cref="Llm.TokenUsage.MaxPlausibleTokensPerSecond"/> is left out.
    /// Empty without the per-reply columns or while the store is unavailable. A model's name is grouped without regard to case (2026-11-27,
    /// the last-3 review's #12: "Qwen3" and "qwen3" are one model's records), and both column parts must be there (#43).
    /// </summary>
    public IReadOnlyList<ModelBest> ModelBestsByModel()
    {
        lock (_gate)
        {
            if (!_extraColumns || !_replyColumns || Open() is not { } connection)
            {
                return [];
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT m, max(speed), max(output_tokens), max(first_token_ms), sum(output_tokens) FROM (
                      SELECT COALESCE(NULLIF(t.model, ''), s.model) AS m, t.output_tokens, t.first_token_ms,
                             CASE WHEN t.output_ms > 0 AND t.timed_output > 0 AND t.timed_output * 1000.0 / t.output_ms <= $cap
                                  THEN t.timed_output * 1000.0 / t.output_ms END AS speed
                      FROM turns t JOIN sessions s ON s.id = t.session_id)
                    GROUP BY m COLLATE NOCASE;
                    SELECT m, context_tokens FROM (
                      SELECT COALESCE(NULLIF(t.model, ''), s.model) AS m, t.context_tokens,
                             ROW_NUMBER() OVER (PARTITION BY COALESCE(NULLIF(t.model, ''), s.model) COLLATE NOCASE ORDER BY t.first_token_ms DESC, t.id DESC) AS rn
                      FROM turns t JOIN sessions s ON s.id = t.session_id WHERE t.first_token_ms IS NOT NULL)
                    WHERE rn = 1;
                    """;
                command.Parameters.AddWithValue("$cap", Llm.TokenUsage.MaxPlausibleTokensPerSecond);
                using var reader = command.ExecuteReader();
                var bests = new Dictionary<string, ModelBest>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    string m = reader.GetString(0);
                    long? longest = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                    bests[m] = new ModelBest(m, reader.IsDBNull(1) ? null : reader.GetDouble(1), longest is > 0 ? longest : null,
                        reader.IsDBNull(3) ? null : reader.GetInt64(3), null, reader.IsDBNull(4) ? 0 : reader.GetInt64(4));
                }

                if (reader.NextResult())
                {
                    while (reader.Read())
                    {
                        string m = reader.GetString(0);
                        if (bests.TryGetValue(m, out var best))
                        {
                            bests[m] = best with { SlowestStartContext = reader.IsDBNull(1) ? null : reader.GetInt64(1) };
                        }
                    }
                }

                return [.. bests.Values];
            }
            catch (SqliteException ex)
            {
                Fail("read the model records", ex);
                return [];
            }
        }
    }

    /// <summary>
    /// The saved turns grouped by the model that answered (2026-11-21, feature idea #7), newest-used first: a turn's own model, else its
    /// session's (a turn from before the column). One grouped query; while the added columns are missing for this run, by the session's
    /// model alone with no speed. Empty while the store is unavailable. Names grouped without regard to case (2026-11-27, the last-3 review's #12).
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
                      FROM turns t JOIN sessions s ON s.id = t.session_id GROUP BY m COLLATE NOCASE ORDER BY max(t.at) DESC, m
                      """
                    : """
                      SELECT s.model AS m, count(*), count(DISTINCT t.session_id), sum(t.tool_calls), sum(min(t.errors, t.tool_calls)),
                             sum(t.cancelled), sum(t.output_tokens), 0, 0, max(t.at), count(*)
                      FROM turns t JOIN sessions s ON s.id = t.session_id GROUP BY m COLLATE NOCASE ORDER BY max(t.at) DESC, m
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
