namespace NeonSidekick.Tests.Fakes;

/// <summary>The eight Oracle tools' names in <c>ChatScreen.OracleTools</c> order (2026-09-30). Pinned.</summary>
internal static class OracleToolNames
{
    public static readonly string[] All =
        ["oracle_connections", "oracle_schemas", "oracle_tables", "oracle_columns", "oracle_describe", "oracle_relationships", "oracle_indexes", "oracle_query"];

    /// <summary>The eight reads and <c>oracle_execute</c> (2026-10-05): what <c>ChatScreen.OracleTools</c> makes; plan mode and the read rule know only the eight.</summary>
    public static readonly string[] WithExecute = [.. All, "oracle_execute"];
}
