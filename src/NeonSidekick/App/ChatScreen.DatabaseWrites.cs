using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sql;

namespace NeonSidekick.App;

// ── The server families' changes (2026-10-05) ─────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The MySQL tools' names cache, for a test that seeds the server's names (2026-10-24, the database review's J6).</summary>
    internal Sql.BoundaryNames MySqlNamesForTests => _mysql.Names;

    /// <summary>
    /// The places "Allow for this session" was picked for: <see cref="DatabaseAllowKey"/>'s, case-blind; forgotten with the session
    /// (<see cref="ForgetSession"/>, <see cref="ForgetAllows"/>: a restored session too since 2026-10-24, review D3).
    /// </summary>
    private readonly HashSet<string> _databaseAllowed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A session allow's key (2026-10-24, the database review's A17, decision DB6): the family, the connection's name, the server it reaches
    /// and the database — an entry re-pointed at another server is asked again, where the old key (family, name, database) kept the allow. Pure.
    /// </summary>
    public static string DatabaseAllowKey(ServerWriteFamily family, ExecuteTarget target)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(target);
        return family.Title + "\n" + target.Connection.Trim() + "\n" + target.Where.Trim() + "\n" + target.Place.Trim();
    }

    /// <summary>
    /// Every database tool's name — the five families', reads and changes (2026-10-24, the database review's J1 and J2, decision DB4): withheld
    /// from advisors, and their results from an advisor's recent lines. Built on first read, so no static-field order across the partial files
    /// can catch a family's set unset.
    /// </summary>
    public static IReadOnlySet<string> DatabaseToolNames => s_databaseToolNames ??= new HashSet<string>(
        SqlToolNames.Concat(OracleToolNames).Concat(MySqlToolNames).Concat(PostgresToolNames).Concat(SqliteToolNames).Concat(SqliteWriteToolNames), StringComparer.Ordinal);

    private static IReadOnlySet<string>? s_databaseToolNames;

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
    /// pane could ask. A statement reaching past the place (<c>DELETE FROM payroll.dbo.salaries</c> under <c>scratch</c>) was asked
    /// about every time from the review of 2026-10-05; since 2026-10-09 each connection is locked to its place and the family's
    /// boundary refuses such a statement before it gets here, so a place is one per connection.
    /// </summary>
    private async Task<bool?> AllowDatabaseWriteAsync(ServerWriteFamily family, ExecuteTarget target, string sql, IReadOnlyList<SqlParameterValue> parameters, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        string key = DatabaseAllowKey(family, target);
        lock (_databaseAllowed)
        {
            if (_databaseAllowed.Contains(key))
            {
                return true;
            }
        }

        // The whole statement for the pane's v, its line ends as the server reads them and its hiding characters escaped (F16), the bound
        // values under it (B5); the caption the same on the PC's pane and the Web UI's ask.
        var whole = Shell.ShellText.WholeCommandLines(new Shell.CommandRequest("sql", Files.NameScrub.Display(ServerWriteText.Lines(sql), keepLineBreaks: true) + ServerWriteText.Bound(parameters), [], IsScript: true));
        string caption = ServerWriteText.AllowCaption(family, target.Connection, target.Place, sql, target.Where, parameters);
        var allow = await AllowBothAsync(ServerWriteText.AllowTitle(family), caption, whole, token => _cameraMenu.AllowAsync(caption, token, ServerWriteText.AllowTitle(family), ServerWriteText.AllowTitle(family), whole), turnToken).ConfigureAwait(false);
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
