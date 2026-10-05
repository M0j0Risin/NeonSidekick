using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Sqlite;

/// <summary>What the SQLite tools may do to a database (<see cref="SqliteModes"/>).</summary>
public enum SqliteMode
{
    /// <summary>Only reads: <c>sqlite_query</c>'s four layers, and <c>sqlite_execute</c> is not offered.</summary>
    ReadOnly,

    /// <summary><c>sqlite_execute</c> is offered too: one change per call, each allowed by the user on the pane.</summary>
    ReadWrite,
}

/// <summary>
/// The setting <c>SQLite mode</c> (2026-10-05, the user's ask): <c>read-only</c>, the tools as they were, or
/// <c>read-write</c>, which adds <c>sqlite_execute</c> — DML, DDL and PRAGMA, one statement per call, each allowed on the
/// Deny / Allow once / Allow for this session pane, and a new database file in the working directory with <c>create</c>.
/// <c>sqlite_query</c> stays read-only whichever is set. The <c>ScreenAskMode</c> shape: <see cref="Resolve"/> is the one place
/// the saved string becomes the enum.
/// </summary>
public static class SqliteModes
{
    public const string Default = "read-only";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["read-only", "read-write"];

    public static bool TryParse(string? text, out SqliteMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": mode = SqliteMode.ReadOnly; return true;
            case "read-write": mode = SqliteMode.ReadWrite; return true;
            default: mode = SqliteMode.ReadOnly; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "read-only" => "the SQLite tools only read",
        "read-write" => "sqlite_execute may change a database or create one, each change allowed on a pane",
        _ => "",
    };

    public static SqliteMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.SqliteMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(SqliteConfigFile.Category, $"{nameof(AppSettingsData.SqliteMode)}='{effective.SqliteMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return SqliteMode.ReadOnly;
    }
}
