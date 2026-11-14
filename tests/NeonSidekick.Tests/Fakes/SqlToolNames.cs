using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The SQL tools in the order <c>ChatScreen.SqlTools</c> offers them (2026-09-23). Pinned once, used by every tool-list assertion.</summary>
public static class SqlToolNames
{
    public static readonly string[] All =
    {
        SqlConnectionsTool.ToolName,
        SqlTablesTool.ToolName,
        SqlColumnsTool.ToolName,
        SqlDescribeTool.ToolName,
        SqlRelationshipsTool.ToolName,
        SqlIndexesTool.ToolName,
        SqlQueryTool.ToolName,
    };

    /// <summary>
    /// The reads, <c>sql_explain</c> and <c>sql_profile</c> (2026-11-14) and <c>sql_execute</c> (2026-10-05): what <c>ChatScreen.SqlTools</c> makes; the read rule knows only
    /// <see cref="All"/> (seven since <c>sql_databases</c> went, 2026-10-09). The rule names its tools whether or not each is offered, so it leaves
    /// explain and profile to their own descriptions: <c>/tools</c> may turn them off.
    /// </summary>
    public static readonly string[] WithExecute = [.. All, SqlExplainTool.ToolName, SqlProfileTool.ToolName, SqlExecuteTool.ToolName];
}
