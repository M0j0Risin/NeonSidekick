namespace NeonSidekick.Files;

/// <summary>
/// The file database families <see cref="DatabaseGuard"/> guards (2026-10-25, the DuckDB tools): each one's files are guarded while its
/// tools are on, so the guard is built from the switches (<c>ChatScreen.DatabaseGuardFor</c>).
/// </summary>
[Flags]
public enum DatabaseFamilies
{
    /// <summary>No family: no guard is built.</summary>
    None = 0,

    /// <summary>SQLite's files: <see cref="DatabaseGuard.Extensions"/>, its journals, its header; <c>sqlite.json</c>'s files.</summary>
    Sqlite = 1,

    /// <summary>DuckDB's files: <see cref="DatabaseGuard.DuckDbExtensions"/>, its <c>.wal</c>, its header; <c>duckdb.json</c>'s files.</summary>
    DuckDb = 2,
}
