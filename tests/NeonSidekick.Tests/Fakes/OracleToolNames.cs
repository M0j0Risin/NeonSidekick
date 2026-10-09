namespace NeonSidekick.Tests.Fakes;

/// <summary>The Oracle tools' read names in <c>ChatScreen.OracleTools</c> order (2026-09-30; <c>oracle_schemas</c> gone 2026-10-09). Pinned.</summary>
internal static class OracleToolNames
{
    public static readonly string[] All =
        ["oracle_connections", "oracle_tables", "oracle_columns", "oracle_describe", "oracle_relationships", "oracle_indexes", "oracle_query"];

    /// <summary>The reads and <c>oracle_execute</c> (2026-10-05): what <c>ChatScreen.OracleTools</c> makes; plan mode and the read rule know only the reads.</summary>
    public static readonly string[] WithExecute = [.. All, "oracle_execute"];
}
