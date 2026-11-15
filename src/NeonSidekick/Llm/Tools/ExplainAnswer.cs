using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The five <c>*_explain</c> tools' shared tail (2026-11-15, the database-tools review's #56, #83 and #86): the parsed plan's summary, or the raw
/// plan's start when it didn't parse, both within <see cref="SqlTool.AnswerChars"/> of the one settings snapshot the call read.
/// </summary>
internal static class ExplainAnswer
{
    /// <summary>
    /// The answer for <paramref name="run"/>: <paramref name="plan"/>'s summary (ending with <paramref name="footer"/>), else
    /// <see cref="QueryPlanText.Unreadable"/> over <paramref name="raw"/>. <paramref name="transform"/>, when given, runs on the finished answer
    /// (DuckDB's sandbox paths put back as the model wrote them, #172).
    /// </summary>
    public static string Of(QueryPlan? plan, SqlRun run, string raw, AppSettingsData effective, string? footer = null, Func<string, string>? transform = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(effective);
        int cap = SqlTool.AnswerChars(effective);
        string answer = plan is not null
            ? QueryPlanText.Answer(plan, run.Connection, run.Database, run.Elapsed, cap, footer)
            : QueryPlanText.Unreadable(run.Connection, run.Database, run.Elapsed, raw, cap);
        return transform is null ? answer : transform(answer);
    }
}
