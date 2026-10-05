using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The SQL tools in the order <c>ChatScreen.SqlTools</c> offers them (2026-09-23). Pinned once, used by every tool-list assertion.</summary>
public static class SqlToolNames
{
    public static readonly string[] All =
    {
        SqlConnectionsTool.ToolName,
        SqlDatabasesTool.ToolName,
        SqlTablesTool.ToolName,
        SqlColumnsTool.ToolName,
        SqlDescribeTool.ToolName,
        SqlRelationshipsTool.ToolName,
        SqlIndexesTool.ToolName,
        SqlQueryTool.ToolName,
    };

    /// <summary>The eight reads and <c>sql_execute</c> (2026-10-05): what <c>ChatScreen.SqlTools</c> makes; plan mode and the read rule know only the eight.</summary>
    public static readonly string[] WithExecute = [.. All, SqlExecuteTool.ToolName];
}
