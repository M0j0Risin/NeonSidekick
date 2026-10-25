using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MySqlConnector;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// The MySQL tools' one door to MySQL and MariaDB (2026-09-30), <see cref="Oracle.OracleAccess"/>'s shape: built once per app, the
/// connections read afresh from <c>mysql.json</c> at every call, a connection opened per call and closed after (MySqlConnector
/// pools it and resets the session on its way back), never held across calls. Read-only, the user's standing ask, under the
/// tools' <see cref="MySqlReadOnlyGate"/>:
/// <list type="number">
/// <item>the session: <c>NO_BACKSLASH_ESCAPES</c> and <c>ANSI_QUOTES</c> stripped from <c>sql_mode</c>, so the server splits strings
/// where the gate's lexer does (a server with either set would read <c>'a\'; DROP …'</c> as a string, then a second statement);
/// a statement time cap on the server itself (<c>max_execution_time</c> on MySQL, <c>max_statement_time</c> on MariaDB);</item>
/// <item>the transaction: <c>START TRANSACTION READ ONLY</c> (ERROR 1792 on any write to a real table, even from a stored
/// function — the spike saw it on MySQL 8.4 and MariaDB 11.8), always rolled back;</item>
/// <item>the driver: no <c>LOAD DATA LOCAL</c>, no user variables (<see cref="MySqlConnectionConfig.Builder"/>).</item>
/// </list>
/// The account is the last (the wizard warns of one that can write). A call is a list of statements, one result set each, all in
/// the one transaction; a statement whose rows pass the cap is killed on the server (<see cref="MySqlCommand.Cancel"/>), since the
/// protocol would otherwise drain the rest. Async all the way, the turn's token passed to every call. The password never leaves
/// the connection string: an error is the server's message, and the log names the connection, never the string.
/// </summary>
public sealed class MySqlAccess
{
    /// <summary>The rows a catalog listing shows at most.</summary>
    public const int MaxCatalogRows = 1000;

    /// <summary>MySQL's answer when <c>max_execution_time</c> stops a SELECT.</summary>
    public const int MySqlStatementTimeout = 3024;

    /// <summary>MariaDB's answer when <c>max_statement_time</c> stops a statement.</summary>
    public const int MariaDbStatementTimeout = 1969;

    /// <summary>The server's answer to a query killed in flight (the cap's cancel).</summary>
    public const int QueryInterrupted = 1317;

    private readonly Func<MySqlCatalog> _catalog;

    /// <param name="catalog">The connections in force; the app's reads the profile's and the home's <c>mysql.json</c> at every call.</param>
    public MySqlAccess(Func<MySqlCatalog> catalog) : this(catalog, null)
    {
    }

    /// <summary>An access over <paramref name="catalog"/> sharing <paramref name="names"/> (a pinned one's, <see cref="Pinned"/>), or a cache of its own.</summary>
    public MySqlAccess(Func<MySqlCatalog> catalog, Sql.BoundaryNames? names)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Names = names ?? new();
    }

    /// <summary>The connections as the files hold them now.</summary>
    public MySqlCatalog Catalog() => _catalog();

    /// <summary>The names the tools' boundary reads before a model's statement, kept a minute per connection (2026-10-09, <see cref="Sql.BoundaryNames"/>).</summary>
    public Sql.BoundaryNames Names { get; }

    /// <summary>
    /// An access over <paramref name="target"/> alone, sharing this one's names cache (2026-10-24, the database review's H11): an <c>_execute</c>
    /// call resolves its connection once and every step after — the boundary, the pane, the run — works on that entry, so a file edited in
    /// between cannot send an allowed change to another server.
    /// </summary>
    public MySqlAccess Pinned(MySqlNamedConnection target) => new(() => new MySqlCatalog([target], []), Names);

    /// <summary>The connection a call means (<see cref="MySqlCatalog.Find"/>), or null with the refusal: none defined, none named among several, or a name that matches none (the names listed).</summary>
    public MySqlNamedConnection? Resolve(string? name, out SqlRun? refused)
    {
        var catalog = Catalog();
        refused = null;
        if (catalog.Connections.Count == 0)
        {
            refused = SqlRun.Refused(SqlOutcome.NoConnections, "");
            return null;
        }

        var found = catalog.Find(name);
        if (found is null)
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, string.Join(", ", catalog.Connections.Select(c => c.Name)), name?.Trim() ?? "");
        }

        return found;
    }

    /// <summary>Whether the server is MariaDB (its version says so), else MySQL.</summary>
    public static bool IsMariaDb(string? serverVersion) => serverVersion?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The connections whose unchecked TLS was noted this run (2026-10-24, decision DB1: once a run each, in the log).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_tlsNoted = new(StringComparer.OrdinalIgnoreCase);

    private static void NoteUncheckedTls(string name, bool checkedTls)
    {
        if (!checkedTls && s_tlsNoted.TryAdd(name, 0))
        {
            DiagnosticLog.Warn(MySqlConfigFile.Category, Sql.SqlText.TlsUncheckedLogLine("MySQL", name));
        }
    }

    /// <summary>Whether a failed open says the password is wrong (1045, access denied): the sign-in guard holds it (2026-10-24, review K1).</summary>
    public static bool WrongPassword(Exception ex) => ex is MySqlException { Number: 1045 };

    /// <summary>
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (or the only one offered
    /// when blank), in the connection's own database (2026-10-09: the one it reaches; a session in any other runs nothing): each one's rows read to
    /// <paramref name="maxRows"/> (one past to learn whether more are left), each statement bound only the parameters it names, all
    /// inside one read-only transaction that is rolled back. A timeout, a failed connect and a server error are outcomes; a
    /// cancelled token throws. The run's <see cref="SqlRun.Database"/> is the database it worked in.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? name, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, out var refused) is not { } target)
        {
            return refused!;
        }

        NoteUncheckedTls(target.Name, target.Config.TlsChecked);
        var secret = MySqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        await using var connection = new MySqlConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("mysql", target.Config.Endpoint, target.Config.User?.Trim(), secret.Value, WrongPassword), connection.OpenAsync, cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.Endpoint, MySqlConfigFile.Family.Tab), target.Name);
            }
        }
        catch (MySqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(MySqlConfigFile.Category, MySqlText.ConnectFailedLogLine(target.Name, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        string workIn = connection.Database ?? "";
        if (!string.Equals(workIn, target.Config.Database?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, MySqlText.WrongDatabase(target.Config.Database?.Trim() ?? "", workIn), target.Name);
        }

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        try
        {
            await ExecuteAsync(connection, null, SessionStatement(connection.ServerVersion, timeoutSeconds), timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (await ModeRefusalAsync(connection, timeoutSeconds, cancellationToken).ConfigureAwait(false) is { } kept)
            {
                return SqlRun.Refused(SqlOutcome.Failed, kept, target.Name);
            }

            // The session read-only as well as the transaction (2026-10-24, found live for the database review's S16: a CREATE TABLE commits the
            // READ ONLY transaction it stands in and then runs, on MySQL 8.4 and MariaDB 11 alike; with the session's access mode read-only
            // the server refuses it, ERROR 1792). A pooled session is reset before its next use.
            await ExecuteAsync(connection, null, ReadOnlySessionStatement, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (string sql in statements)
                {
                    // One deadline for the whole call (2026-10-24, the database review's O13: each statement had the full timeout).
                    if (SqlDeadline.Left(watch, timeoutSeconds) is not (> 0 and var left))
                    {
                        return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
                    }

                    grids.Add(await ReadAsync(connection, transaction, sql, parameters, maxRows, left, cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
            }
        }
        catch (MySqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (MySqlException ex) when (IsTimeout(ex))
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is MySqlException inner && IsTimeout(inner))
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (MySqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed);
    }

    /// <summary>
    /// <c>mysql_execute</c>'s run (2026-10-05, <c>MySQL mode</c> <c>read-write</c>): one statement the write gate passed, on a connection
    /// whose entry says <c>"access": "readwrite"</c> (any other is <see cref="SqlOutcome.ReadOnlyConnection"/>, whatever the caller
    /// checked), in the connection's own database. The session as for a read — <c>sql_mode</c> without
    /// <c>NO_BACKSLASH_ESCAPES</c>/<c>ANSI_QUOTES</c>, so the server splits strings as the gate does, and the statement cap (MariaDB's caps
    /// every statement, MySQL's a SELECT alone; the command timeout stops the rest, the driver killing the query) — and no transaction:
    /// autocommit, so the statement commits on its own, atomically (DDL commits anyway). The rows a statement gives back (MariaDB's
    /// <c>RETURNING</c>, <c>ANALYZE TABLE</c>'s report) are read to <paramref name="maxRows"/> and the rest drained, never killed: a kill
    /// would undo the statement. <see cref="SqlRun.Changes"/> is the rows the server counts.
    /// </summary>
    public async Task<SqlRun> ExecuteAsync(string? name, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, out var refused) is not { } target)
        {
            return refused!;
        }

        if (!target.Config.IsReadWrite)
        {
            return SqlRun.Refused(SqlOutcome.ReadOnlyConnection, "", target.Name);
        }

        NoteUncheckedTls(target.Name, target.Config.TlsChecked);
        var secret = MySqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        await using var connection = new MySqlConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("mysql", target.Config.Endpoint, target.Config.User?.Trim(), secret.Value, WrongPassword), connection.OpenAsync, cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.Endpoint, MySqlConfigFile.Family.Tab), target.Name);
            }
        }
        catch (MySqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(MySqlConfigFile.Category, MySqlText.ConnectFailedLogLine(target.Name, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        string workIn = connection.Database ?? "";
        if (!string.Equals(workIn, target.Config.Database?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, MySqlText.WrongDatabase(target.Config.Database?.Trim() ?? "", workIn), target.Name);
        }

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        int? changes;
        try
        {
            await ExecuteAsync(connection, null, SessionStatement(connection.ServerVersion, timeoutSeconds), timeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (await ModeRefusalAsync(connection, timeoutSeconds, cancellationToken).ConfigureAwait(false) is { } kept)
            {
                return SqlRun.Refused(SqlOutcome.Failed, kept, target.Name);
            }

            // Autocommit set here, never the server's default trusted (2026-10-24, the database review's H4: under autocommit=0 or an
            // init_connect that turns it off, the change said it was made and was audited, then the pool's reset rolled it back).
            await ExecuteAsync(connection, null, AutocommitStatement, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            foreach (var parameter in Bound(sql, parameters))
            {
                command.Parameters.Add(Bind(parameter));
            }

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.FieldCount > 0)
                {
                    grids.Add(await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false));
                }

                await reader.CloseAsync().ConfigureAwait(false);
                changes = reader.RecordsAffected >= 0 ? reader.RecordsAffected : null;
            }
        }
        catch (MySqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (MySqlException ex) when (IsTimeout(ex))
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is MySqlException inner && IsTimeout(inner))
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (MySqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed) { Changes = changes };
    }

    /// <summary>What a change's session runs before its statement, so the statement commits on its own (H4).</summary>
    public const string AutocommitStatement = "SET SESSION autocommit = 1";

    /// <summary>The read path's session access mode (S16): every transaction of the session read-only, the implicit ones DDL makes too.</summary>
    public const string ReadOnlySessionStatement = "SET SESSION TRANSACTION READ ONLY";

    /// <summary>
    /// The session's own statement, before the transaction: <c>sql_mode</c> without <c>NO_BACKSLASH_ESCAPES</c> and
    /// <c>ANSI_QUOTES</c> (an empty slot the two leave is one the server accepts), and the statement time cap in the server's
    /// own variable and unit. Pure.
    /// </summary>
    public static string SessionStatement(string? serverVersion, int timeoutSeconds)
    {
        string cap = IsMariaDb(serverVersion)
            ? "SESSION max_statement_time = " + timeoutSeconds.ToString(CultureInfo.InvariantCulture)
            : "SESSION max_execution_time = " + (timeoutSeconds * 1000L).ToString(CultureInfo.InvariantCulture);
        // group_concat_max_len raised (2026-10-24, the database review's E12): MySQL's 1,024 bytes cut an index's key list silently.
        return "SET SESSION sql_mode = " + ModeStripped + ", " + cap + ", SESSION group_concat_max_len = 1048576";
    }

    /// <summary>
    /// The modes the gate's lexing needs off: the two that change how a string or a name is read, and every combination mode that turns
    /// one of them on (2026-10-24, the database review's E1: MySQL's <c>ANSI</c> and MariaDB's <c>ORACLE</c>, <c>MSSQL</c>, <c>DB2</c>,
    /// <c>POSTGRESQL</c>, <c>MAXDB</c> each carry <c>ANSI_QUOTES</c>, and with the word left in <c>@@sql_mode</c> the SET turned it back on — the
    /// gate then read <c>"…"</c> as a string where the server read a name).
    /// </summary>
    public static readonly IReadOnlyList<string> StrippedModes = ["ANSI_QUOTES", "NO_BACKSLASH_ESCAPES", "ANSI", "ORACLE", "MSSQL", "DB2", "POSTGRESQL", "MAXDB"];

    /// <summary>The session's mode with every one of <see cref="StrippedModes"/> taken out as a whole word.</summary>
    public static readonly string ModeStripped =
        "TRIM(BOTH ',' FROM " + StrippedModes.Aggregate("CONCAT(',', @@SESSION.sql_mode, ',')", (expression, mode) => "REPLACE(" + expression + ", '," + mode + ",', ',')") + ")";

    /// <summary>
    /// The session's mode read back after the SET, the call refused unless every one of <see cref="StrippedModes"/> is gone (2026-10-24, review
    /// E1, mechanism 7: a layer that did not hold refuses the call rather than run on a gate that reads the text another way). Null when it held.
    /// </summary>
    private static async Task<string?> ModeRefusalAsync(MySqlConnection connection, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT @@SESSION.sql_mode", connection) { CommandTimeout = timeoutSeconds };
        string mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "";
        return MySqlText.ModeKept(mode, StrippedModes) is { } kept ? MySqlText.SessionModeKept(kept) : null;
    }

    /// <summary>Whether <paramref name="ex"/> is a timeout: the driver's command timeout, or the server's statement cap.</summary>
    public static bool IsTimeout(MySqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.ErrorCode == MySqlErrorCode.CommandTimeoutExpired || ex.Number is MySqlStatementTimeout or MariaDbStatementTimeout;
    }

    /// <summary>One statement's rows: the parameters it names bound, read to the cap, the query killed on the server when more are left (its interrupt answer then expected, not an error).</summary>
    private static async Task<SqlGrid> ReadAsync(MySqlConnection connection, MySqlTransaction transaction, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds };
        foreach (var parameter in Bound(sql, parameters))
        {
            command.Parameters.Add(Bind(parameter));
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        SqlGrid? grid = null;
        try
        {
            grid = await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false);
            if (grid.More)
            {
                command.Cancel();
            }
        }
        finally
        {
            try
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            catch (MySqlException ex) when (grid is { More: true } && (ex.Number == QueryInterrupted || ex.ErrorCode == MySqlErrorCode.QueryInterrupted))
            {
                // The kill the cap sent, answered on the way out: the rows read are the result.
            }
        }

        return grid!;
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The parameters a statement takes: those it names (a model's <c>@@version</c> is no parameter), or every one when the gate's lexer cannot
    /// read it (2026-10-24, found live: the app's own CHECK query carries MariaDB's <c>/*M!100310 … */</c>, which the gate refuses, and its
    /// <c>@db</c> went unbound). A model's text that does not lex never gets here: its gate refused it first.
    /// </summary>
    private static IEnumerable<SqlParameterValue> Bound(string sql, IReadOnlyList<SqlParameterValue> parameters)
    {
        if (MySqlReadOnlyGate.Tokenize(sql, out _) is null)
        {
            return parameters;
        }

        var named = MySqlReadOnlyGate.Binds(sql);
        return parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Rolls back what is left of the transaction; a connection the server already dropped has nothing to roll back.</summary>
    private static async Task RollBackAsync(MySqlTransaction transaction)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MySqlException or InvalidOperationException)
        {
            // The connection is gone, or the transaction already ended with the error.
        }
    }

    /// <summary>
    /// A MySQL date as it is (2026-10-24, the database review's N10): a real one as every date is written, a zero or partly zero one as MySQL
    /// writes it (<c>0000-00-00</c>), never a real-looking <c>0001-01-01</c>.
    /// </summary>
    public static string DateCell(MySqlDateTime date) =>
        date.IsValidDateTime ? SqlText.Cell(date.GetDateTime())
            : string.Create(CultureInfo.InvariantCulture, $"{date.Year:0000}-{date.Month:00}-{date.Day:00}") + (date.Hour + date.Minute + date.Second + date.Microsecond == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" {date.Hour:00}:{date.Minute:00}:{date.Second:00}"));

    /// <summary>
    /// One cell as text: <see cref="SqlText.Cell"/> for what .NET holds; a <c>DECIMAL</c> past <see cref="decimal"/> (up to 65
    /// digits) as its <see cref="MySqlDecimal"/> text, every digit kept; what the client cannot read as <see cref="MySqlText.Unreadable"/>.
    /// </summary>
    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? SqlText.Null : reader.GetValue(ordinal) switch
            {
                MySqlDateTime date => DateCell(date),
                var value => SqlText.Cell(value),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            try
            {
                return reader is MySqlDataReader mysql ? SqlText.Cell(mysql.GetMySqlDecimal(ordinal).ToString()) : MySqlText.Unreadable(reader.GetDataTypeName(ordinal));
            }
            catch (Exception inner) when (inner is not (DbException or OperationCanceledException or OutOfMemoryException))
            {
                return MySqlText.Unreadable(reader.GetDataTypeName(ordinal));
            }
        }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            return MySqlText.Unreadable(reader.GetDataTypeName(ordinal));
        }
    }

    /// <summary>The server's message with its number — <c>Error 1146: Table 'shop.x' doesn't exist</c> is what the model needs to fix the text.</summary>
    public static string Message(MySqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string text = LogText.Excerpt(ex.Message.ReplaceLineEndings(" "), 600);
        return ex.Number > 0 ? MySqlText.ServerError(ex.Number, text) : text;
    }

    /// <summary>A parameter typed by its value, bound as <c>@name</c>: text as <c>VARCHAR</c>, whole numbers as <c>BIGINT</c>, decimals exact, floats as <c>DOUBLE</c>, a boolean as 1 or 0.</summary>
    public static MySqlParameter Bind(SqlParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = "@" + value.Name;
        return value.Value switch
        {
            null => new MySqlParameter(name, MySqlDbType.VarChar) { Value = DBNull.Value },
            string s => new MySqlParameter(name, MySqlDbType.VarChar) { Value = s },
            long l => new MySqlParameter(name, MySqlDbType.Int64) { Value = l },
            int i => new MySqlParameter(name, MySqlDbType.Int32) { Value = i },
            decimal d => new MySqlParameter(name, MySqlDbType.NewDecimal) { Value = d },
            double f => new MySqlParameter(name, MySqlDbType.Double) { Value = f },
            bool b => new MySqlParameter(name, MySqlDbType.Bool) { Value = b },
            var other => new MySqlParameter(name, other),
        };
    }

    /// <summary>
    /// A table reference split into database and name: <c>shop.orders</c> is <c>(shop, orders)</c>, <c>orders</c> is
    /// <c>(null, orders)</c>, <c>`my.db`.`t`</c> keeps its dot inside the backticks (a doubled backtick is one). Null when it is not
    /// one or two names, or one is empty.
    /// </summary>
    public static (string? Database, string Name)? Split(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        string text = reference.Trim();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '`')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '`')
                {
                    current.Append('`');
                    i++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (c == '.' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            // Spaces count only inside the quotes (2026-10-24, the database review's O14: the parts were trimmed after unquoting, so a
            // quoted name with a leading or trailing space lost it and named another object).
            if (!quoted && char.IsWhiteSpace(c))
            {
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        if (quoted || parts.Count > 2 || parts.Any(p => p.Length == 0))
        {
            return null;
        }

        return parts.Count == 1 ? (null, parts[0]) : (parts[0], parts[1]);
    }
}
