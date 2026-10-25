using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.DuckDb;

/// <summary>What the DuckDB tools may do to a database (<see cref="DuckDbModes"/>).</summary>
public enum DuckDbMode
{
    /// <summary>Only reads: <c>duckdb_query</c>'s layers, and <c>duckdb_execute</c> is not offered.</summary>
    ReadOnly,

    /// <summary><c>duckdb_execute</c> is offered too: one change per call, each allowed by the user on the pane.</summary>
    ReadWrite,
}

/// <summary>
/// The setting <c>DuckDB mode</c> (2026-10-25), <c>SQLite mode</c>'s twin: <c>read-only</c>, or <c>read-write</c>, which adds
/// <c>duckdb_execute</c> — one statement per call, each allowed on the Deny / Allow once / Allow for this session pane, and a new
/// database file in the working directory with <c>create</c>. <c>duckdb_query</c> stays read-only whichever is set. <see cref="Resolve"/>
/// is the one place the saved string becomes the enum.
/// </summary>
public static class DuckDbModes
{
    public const string Default = "read-only";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["read-only", "read-write"];

    public static bool TryParse(string? text, out DuckDbMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": mode = DuckDbMode.ReadOnly; return true;
            case "read-write": mode = DuckDbMode.ReadWrite; return true;
            default: mode = DuckDbMode.ReadOnly; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "read-only" => "the DuckDB tools only read",
        "read-write" => "duckdb_execute may change a database or create one, each change asking you first",
        _ => "",
    };

    public static DuckDbMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.DuckDbMode, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(DuckDbConfigFile.Category, $"{nameof(AppSettingsData.DuckDbMode)}='{effective.DuckDbMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return DuckDbMode.ReadOnly;
    }
}
