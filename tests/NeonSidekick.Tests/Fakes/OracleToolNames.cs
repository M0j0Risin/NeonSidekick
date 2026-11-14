namespace NeonSidekick.Tests.Fakes;

/// <summary>The Oracle tools' read names in <c>ChatScreen.OracleTools</c> order (2026-09-30; <c>oracle_schemas</c> gone 2026-10-09). Pinned.</summary>
internal static class OracleToolNames
{
    public static readonly string[] All =
        ["oracle_connections", "oracle_tables", "oracle_columns", "oracle_describe", "oracle_relationships", "oracle_indexes", "oracle_query"];

    /// <summary>
    /// The reads, <c>oracle_profile</c> (2026-11-14) and <c>oracle_execute</c> (2026-10-05): what <c>ChatScreen.OracleTools</c> makes; the read rule
    /// knows only <see cref="All"/>, leaving profile to its own description (the rule names its tools whether or not each is offered).
    /// </summary>
    public static readonly string[] WithExecute = [.. All, "oracle_profile", "oracle_execute"];
}
