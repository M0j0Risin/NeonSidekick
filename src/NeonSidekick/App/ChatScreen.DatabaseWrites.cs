using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sql;

namespace NeonSidekick.App;

// ── The server families' changes (2026-10-05) ─────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The places "Allow for this session" was picked for: <c>family\nconnection\nplace</c>, case-blind; forgotten with the session (<see cref="ForgetSession"/>).</summary>
    private readonly HashSet<string> _databaseAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each server family's <c>_execute</c> tool by name, and the flag it sets in <see cref="ServerWritesOf"/>.</summary>
    public static readonly IReadOnlyDictionary<string, ServerWrites> ServerWriteToolFlags = new Dictionary<string, ServerWrites>(StringComparer.Ordinal)
    {
        [PostgresExecuteTool.ToolName] = ServerWrites.Postgres,
        [OracleExecuteTool.ToolName] = ServerWrites.Oracle,
        [SqlExecuteTool.ToolName] = ServerWrites.Sql,
        [MySqlExecuteTool.ToolName] = ServerWrites.MySql,
    };

    /// <summary>The families whose <c>_execute</c> tool is among <paramref name="tools"/>: what the rules' write sentences ride on.</summary>
    public static ServerWrites ServerWritesOf(IEnumerable<AIFunction> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var writes = ServerWrites.None;
        foreach (var tool in tools)
        {
            if (ServerWriteToolFlags.TryGetValue(tool.Name, out var flag))
            {
                writes |= flag;
            }
        }

        return writes;
    }

    /// <summary>
    /// Whether a family's <c>_execute</c> tool may be offered (2026-10-05, the families' <c>*ToolsFor</c>): a pane to ask on, its mode
    /// <c>read-write</c>, a kind ticked, and a <c>readwrite</c> connection among those offered. Pure.
    /// </summary>
    public static bool WritesOffered(bool pane, string? mode, IReadOnlyList<string>? kinds, bool readWriteConnection) =>
        pane && DatabaseWriteModes.IsReadWrite(mode) && ServerStatementKinds.Resolve(kinds).Count > 0 && readWriteConnection;

    /// <summary>
    /// A server family's <c>_execute</c> allow (2026-10-05, the user's pick: <see cref="AllowSqliteWriteAsync"/>'s way, per connection
    /// and database), on the turn task: a place allowed for the session runs at once; else the camera's allow pane — Deny, Allow once,
    /// Allow for this session — titled <see cref="ServerWriteText.AllowTitle"/>, its caption the place and the statement. Null when no
    /// pane could ask. A statement that qualifies a name with anything but the place (<see cref="ServerWriteFamily.NamesElsewhere"/>,
    /// the review of 2026-10-05: <c>DELETE FROM payroll.dbo.salaries</c> under an allow for <c>scratch</c>) is asked about every
    /// time, its caption saying why: the place is what the call names, not what the text reaches.
    /// </summary>
    private async Task<bool?> AllowDatabaseWriteAsync(ServerWriteFamily family, string connection, string place, string sql, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        string key = family.Title + "\n" + connection + "\n" + place;
        bool elsewhere = family.NamesElsewhere?.Invoke(sql, place) == true;
        lock (_databaseAllowed)
        {
            if (!elsewhere && _databaseAllowed.Contains(key))
            {
                return true;
            }
        }

        var allow = await RunCameraPaneAsync(token => _cameraMenu.AllowAsync(ServerWriteText.AllowCaption(family, connection, place, sql, elsewhere), token, ServerWriteText.AllowTitle(family)), null, turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return null;
        }

        if (choice == CameraAllow.Session)
        {
            lock (_databaseAllowed)
            {
                _databaseAllowed.Add(key);
            }
        }

        return choice != CameraAllow.Deny;
    }
}
