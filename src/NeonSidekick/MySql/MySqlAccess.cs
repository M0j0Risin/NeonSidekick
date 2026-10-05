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
    public MySqlAccess(Func<MySqlCatalog> catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The connections as the files hold them now.</summary>
    public MySqlCatalog Catalog() => _catalog();

    /// <summary>The connection a call means (<see cref="MySqlCatalog.Find"/>), or null with the refusal: none defined, or a name that matches none (the names listed).</summary>
    public MySqlNamedConnection? Resolve(string? name, string? defaultName, out SqlRun? refused)
    {
        var catalog = Catalog();
        refused = null;
        if (catalog.Connections.Count == 0)
        {
            refused = SqlRun.Refused(SqlOutcome.NoConnections, "");
            return null;
        }

        var found = catalog.Find(name, defaultName);
        if (found is null)
        {
            refused = SqlRun.Refused(SqlOutcome.UnknownConnection, string.Join(", ", catalog.Connections.Select(c => c.Name)), name?.Trim() ?? "");
        }

        return found;
    }

    /// <summary>Whether the server is MariaDB (its version says so), else MySQL.</summary>
    public static bool IsMariaDb(string? serverVersion) => serverVersion?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (<paramref name="defaultName"/> or the first
    /// when blank), in <paramref name="database"/> when given (else the connection's own): each one's rows read to
    /// <paramref name="maxRows"/> (one past to learn whether more are left), each statement bound only the parameters it names, all
    /// inside one read-only transaction that is rolled back. A timeout, a failed connect and a server error are outcomes; a
    /// cancelled token throws. The run's <see cref="SqlRun.Database"/> is the database it worked in (empty with none in force).
    /// </summary>
    public async Task<SqlRun> RunAsync(string? name, string? defaultName, string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = MySqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        await using var connection = new MySqlConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        string workIn = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(database))
            {
                await connection.ChangeDatabaseAsync(database.Trim(), cancellationToken).ConfigureAwait(false);
            }

            workIn = connection.Database ?? "";
            await ExecuteAsync(connection, null, SessionStatement(connection.ServerVersion, timeoutSeconds), timeoutSeconds, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (string sql in statements)
                {
                    grids.Add(await ReadAsync(connection, transaction, sql, parameters, maxRows, timeoutSeconds, cancellationToken).ConfigureAwait(false));
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
    /// checked), in <paramref name="database"/> when given. The session as for a read — <c>sql_mode</c> without
    /// <c>NO_BACKSLASH_ESCAPES</c>/<c>ANSI_QUOTES</c>, so the server splits strings as the gate does, and the statement cap (MariaDB's caps
    /// every statement, MySQL's a SELECT alone; the command timeout stops the rest, the driver killing the query) — and no transaction:
    /// autocommit, so the statement commits on its own, atomically (DDL commits anyway). The rows a statement gives back (MariaDB's
    /// <c>RETURNING</c>, <c>ANALYZE TABLE</c>'s report) are read to <paramref name="maxRows"/> and the rest drained, never killed: a kill
    /// would undo the statement. <see cref="SqlRun.Changes"/> is the rows the server counts.
    /// </summary>
    public async Task<SqlRun> ExecuteAsync(string? name, string? defaultName, string? database, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        if (!target.Config.IsReadWrite)
        {
            return SqlRun.Refused(SqlOutcome.ReadOnlyConnection, "", target.Name);
        }

        var secret = MySqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        await using var connection = new MySqlConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        string workIn = "";
        int? changes;
        try
        {
            if (!string.IsNullOrWhiteSpace(database))
            {
                await connection.ChangeDatabaseAsync(database.Trim(), cancellationToken).ConfigureAwait(false);
            }

            workIn = connection.Database ?? "";
            await ExecuteAsync(connection, null, SessionStatement(connection.ServerVersion, timeoutSeconds), timeoutSeconds, cancellationToken).ConfigureAwait(false);
            await using var command = new MySqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            var named = MySqlReadOnlyGate.Binds(sql);
            foreach (var parameter in parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
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
        return "SET SESSION sql_mode = REPLACE(REPLACE(@@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES', ''), 'ANSI_QUOTES', ''), " + cap;
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
        var named = MySqlReadOnlyGate.Binds(sql);
        foreach (var parameter in parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
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
    /// One cell as text: <see cref="SqlText.Cell"/> for what .NET holds; a <c>DECIMAL</c> past <see cref="decimal"/> (up to 65
    /// digits) as its <see cref="MySqlDecimal"/> text, every digit kept; what the client cannot read as <see cref="MySqlText.Unreadable"/>.
    /// </summary>
    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? SqlText.Null : SqlText.Cell(reader.GetValue(ordinal));
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

            current.Append(c);
        }

        parts.Add(current.ToString());
        if (quoted || parts.Count > 2 || parts.Any(p => p.Trim().Length == 0))
        {
            return null;
        }

        return parts.Count == 1 ? (null, parts[0].Trim()) : (parts[0].Trim(), parts[1].Trim());
    }
}
