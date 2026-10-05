using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>What a server family's tools may do to its databases (<see cref="DatabaseWriteModes"/>).</summary>
public enum DatabaseWriteMode
{
    /// <summary>Only reads: the family's read-only layers, and its <c>_execute</c> tool is not offered.</summary>
    ReadOnly,

    /// <summary>The family's <c>_execute</c> tool is offered too, on its <c>readwrite</c> connections: one change per call, each allowed by the user on the pane.</summary>
    ReadWrite,
}

/// <summary>
/// The settings <c>SQL mode</c>, <c>Oracle mode</c>, <c>MySQL mode</c> and <c>PostgreSQL mode</c> (2026-10-05, the user's ask:
/// SQLite's read-write mode mirrored for the server families): <c>read-only</c>, the tools as they were, or <c>read-write</c>, which
/// adds the family's <c>_execute</c> tool on the connections whose entry says <c>"access": "readwrite"</c> (the user's two-key call,
/// UNC's shape) — one statement per call, of the kinds the family's <c>statements allowed</c> ticks, each allowed on the Deny / Allow
/// once / Allow for this session pane. The read tools stay read-only whichever is set. <c>SqliteModes</c>' shape, shared by the four;
/// <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class DatabaseWriteModes
{
    public const string Default = "read-only";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["read-only", "read-write"];

    public static bool TryParse(string? text, out DatabaseWriteMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "read-only": mode = DatabaseWriteMode.ReadOnly; return true;
            case "read-write": mode = DatabaseWriteMode.ReadWrite; return true;
            default: mode = DatabaseWriteMode.ReadOnly; return false;
        }
    }

    /// <summary>The menu hint next to a mode for <paramref name="family"/>. Pinned.</summary>
    public static string Describe(string name, ServerWriteFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return name switch
        {
            "read-only" => $"the {family.Title} tools only read",
            "read-write" => $"{family.ToolName} may change a readwrite connection's database, each change allowed on a pane",
            _ => "",
        };
    }

    /// <summary>The mode <paramref name="saved"/> names; anything else is read-only, with a warning naming <paramref name="family"/>'s setting.</summary>
    public static DatabaseWriteMode Resolve(string? saved, ServerWriteFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (TryParse(saved, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(family.Category, $"{family.ModeKey}='{saved}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return DatabaseWriteMode.ReadOnly;
    }

    /// <summary>Whether <paramref name="saved"/> is <c>read-write</c>, quietly (the menu's value; <see cref="Resolve"/> warns).</summary>
    public static bool IsReadWrite(string? saved) => TryParse(saved, out var mode) && mode == DatabaseWriteMode.ReadWrite;
}
