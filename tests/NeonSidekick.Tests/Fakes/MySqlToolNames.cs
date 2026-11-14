namespace NeonSidekick.Tests.Fakes;

/// <summary>The MySQL tools' read names in <c>ChatScreen.MySqlTools</c> order (2026-09-30; <c>mysql_databases</c> gone 2026-10-09). Pinned.</summary>
internal static class MySqlToolNames
{
    public static readonly string[] All =
        ["mysql_connections", "mysql_tables", "mysql_columns", "mysql_describe", "mysql_relationships", "mysql_indexes", "mysql_query"];

    /// <summary>
    /// The reads, <c>mysql_explain</c> and <c>mysql_profile</c> (2026-11-14) and <c>mysql_execute</c> (2026-10-05): what <c>ChatScreen.MySqlTools</c> makes; the read rule knows
    /// only <see cref="All"/>, leaving explain and profile to their own descriptions (the rule names its tools whether or not each is offered).
    /// </summary>
    public static readonly string[] WithExecute = [.. All, "mysql_explain", "mysql_profile", "mysql_execute"];
}
