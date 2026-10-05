namespace NeonSidekick.Tests.Fakes;

/// <summary>The eight MySQL tools' names in <c>ChatScreen.MySqlTools</c> order (2026-09-30). Pinned.</summary>
internal static class MySqlToolNames
{
    public static readonly string[] All =
        ["mysql_connections", "mysql_databases", "mysql_tables", "mysql_columns", "mysql_describe", "mysql_relationships", "mysql_indexes", "mysql_query"];

    /// <summary>The eight reads and <c>mysql_execute</c> (2026-10-05): what <c>ChatScreen.MySqlTools</c> makes; plan mode and the read rule know only the eight.</summary>
    public static readonly string[] WithExecute = [.. All, "mysql_execute"];
}
