using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>Why a call did not return rows. <see cref="Ok"/> is the one that did.</summary>
public enum SqlOutcome
{
    Ok,
    NoConnections,
    UnknownConnection,
    ConnectFailed,
    Timeout,
    Failed,
}

/// <summary>One named value bound as <c>@Name</c>.</summary>
public sealed record SqlParameterValue(string Name, object? Value);

/// <summary>
/// One result set as text cells: the column names, the rows read (at most the cap), and whether the server had
/// more — read one row past the cap, never counted to the end.
/// </summary>
public sealed record SqlGrid(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows, bool More)
{
    /// <summary>
    /// Reads the current result set of <paramref name="reader"/>: up to <paramref name="maxRows"/> rows, then one
    /// more to learn whether any is left. Each cell through <see cref="SqlText.Cell"/>; a value the client
    /// cannot materialise (a CLR type such as <c>geography</c> or <c>hierarchyid</c>, whose assembly the app
    /// does not carry; a <c>decimal(38)</c> past <see cref="decimal"/>'s range) is its provider value's text or
    /// <see cref="SqlText.Unreadable"/>, never a failed call. <paramref name="readCell"/> reads a cell in place of that
    /// (2026-09-30: <see cref="Oracle.OracleAccess"/>'s, for Oracle's own types).
    /// </summary>
    public static async Task<SqlGrid> ReadAsync(DbDataReader reader, int maxRows, CancellationToken cancellationToken, Func<DbDataReader, int, string>? readCell = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
        {
            string name = reader.GetName(i);
            columns[i] = string.IsNullOrEmpty(name) ? SqlText.UnnamedColumn(i) : name;
        }

        var rows = new List<string[]>();
        bool more = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == maxRows)
            {
                more = true;
                break;
            }

            var cells = new string[columns.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = (readCell ?? ReadCell)(reader, i);
            }

            rows.Add(cells);
        }

        return new SqlGrid(columns, rows, more);
    }

    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? SqlText.Null : SqlText.Cell(reader.GetValue(ordinal));
        }
        catch (OverflowException)
        {
            // decimal(38, x) past System.Decimal: SqlDecimal keeps every digit, and its text is invariant.
            return SqlText.Cell(reader.GetProviderSpecificValue(ordinal)?.ToString() ?? "");
        }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            // A CLR user type the app carries no assembly for (geography, geometry, hierarchyid): the type load fails.
            // The reader names it in full (AdventureWorks2022.sys.geography); the last part is what a query writes.
            string type = reader.GetDataTypeName(ordinal);
            return SqlText.Unreadable(type[(type.LastIndexOf('.') + 1)..]);
        }
    }
}

/// <summary>
/// What one run returned: the outcome, the detail an error carries, the connection and database it ran on,
/// the result sets and the time the server took.
/// </summary>
public sealed record SqlRun(SqlOutcome Outcome, string Detail, string Connection, string Database, IReadOnlyList<SqlGrid> Grids, TimeSpan Elapsed)
{
    public static SqlRun Refused(SqlOutcome outcome, string detail, string connection = "") => new(outcome, detail, connection, "", [], TimeSpan.Zero);

    /// <summary>The rows a change changed (2026-10-05, <c>sqlite_execute</c>'s <c>sqlite3_changes</c>); null for a read.</summary>
    public int? Changes { get; init; }
}

/// <summary>
/// The SQL tools' one door to SQL Server (2026-09-23), the <see cref="Git.GitAccess"/> shape: built once per app,
/// the connections read afresh from <c>sql.json</c> at every call (<see cref="SqlConfigFile.LoadCatalog"/>), a
/// connection opened per call and closed after (SqlClient pools the socket), never held across calls — the
/// reflection job may call a tool while a turn runs. Every batch runs inside a transaction that is rolled back,
/// whatever it did: the second layer under <see cref="SqlReadOnlyGate"/>, which the tools consult before a
/// model's text gets here (the catalog batches are the app's own). Async all the way, the turn's token passed
/// to every call, so ESC cancels a running query on the server. The password never leaves the connection
/// string: an error is the server's message, and the log names the connection, never the string.
/// </summary>
public sealed class SqlAccess
{
    /// <summary>
    /// SqlClient's native network layer on Windows (TDS over TCP and named pipes, integrated sign-in), from
    /// Microsoft.Data.SqlClient.SNI.runtime beside the exe. <see cref="App.SmokeChecks.RequiredNativeLibraries"/>
    /// names it; <c>sql:parse-and-sni</c> loads it.
    /// </summary>
    public const string NativeLibraryFileName = "Microsoft.Data.SqlClient.SNI.dll";

    /// <summary>The rows a catalog listing (tables, relationships, …) shows at most.</summary>
    public const int MaxCatalogRows = 1000;

    private readonly Func<SqlCatalog> _catalog;

    /// <param name="catalog">The connections in force; the app's reads the profile's and the home's <c>sql.json</c> at every call.</param>
    public SqlAccess(Func<SqlCatalog> catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The connections as the files hold them now.</summary>
    public SqlCatalog Catalog() => _catalog();

    /// <summary>
    /// The connection a call means (<see cref="SqlCatalog.Find"/>), or null with the <c>Error:</c> sentence:
    /// none defined, or a name that matches none (the names listed).
    /// </summary>
    public SqlNamedConnection? Resolve(string? name, string? defaultName, out SqlRun? refused)
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

    /// <summary>
    /// Runs <paramref name="sql"/> on the connection <paramref name="name"/> names (<paramref name="defaultName"/> or
    /// the first when blank), in <paramref name="database"/> when given: every result set read to
    /// <paramref name="maxRows"/> (<see cref="SqlGrid.ReadAsync"/>), the command cancelled at the first set that has
    /// more — so the server stops, and the reader never drains a million rows on dispose — and the transaction
    /// rolled back. A timeout, a failed connect and a server error are outcomes; a cancelled token throws.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? name, string? defaultName, string? database, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = SqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(database, secret.Value);
        string catalogName = builder.InitialCatalog;
        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            if (target.Config.IsRunAs)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return SqlRun.Refused(SqlOutcome.ConnectFailed, WindowsCredentials.NotWindows, target.Name);
                }

                if (await OpenAsAsync(connection, target, secret.Value!, cancellationToken).ConfigureAwait(false) is { } refusedLogon)
                {
                    return refusedLogon;
                }
            }
            else
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(SqlConfigFile.Category, SqlText.ConnectFailedLogLine(target.Name, catalogName, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        string databaseName = connection.Database;
        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        try
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds };
                foreach (var parameter in parameters)
                {
                    command.Parameters.Add(Bind(parameter));
                }

                await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    do
                    {
                        if (reader.FieldCount == 0)
                        {
                            continue;
                        }

                        var grid = await SqlGrid.ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
                        grids.Add(grid);
                        if (grid.More)
                        {
                            command.Cancel();
                            break;
                        }
                    }
                    while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            finally
            {
                await RollBackAsync(transaction).ConfigureAwait(false);
            }
        }
        catch (SqlException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), target.Name, databaseName, grids, watch.Elapsed);
        }
        catch (SqlException ex) when (grids.Count > 0 && grids[^1].More && ex.Number == 0)
        {
            // The cancel the cap sent, answered as an error on the way out: the rows read are the result.
        }
        catch (SqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, databaseName, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, databaseName, grids, watch.Elapsed);
    }

    /// <summary>
    /// Opens <paramref name="connection"/> signed in as the <c>runas</c> account (later on 2026-09-23): a
    /// <c>NEW_CREDENTIALS</c> token (<see cref="WindowsCredentials.LogonNetOnly"/>), and the <b>synchronous</b> open run
    /// impersonated on a worker thread — the sign-in's SSPI handshake takes the calling thread's token, and a sync open
    /// keeps the whole handshake on the one thread that holds it (the async open's continuations may land on others).
    /// The connect timeout bounds it, and ESC waits it out: abandoning the open would dispose the token and the connection under the thread still using them. Only the sign-in is impersonated: once open, the
    /// batch runs as every other. Null when open; else the refusal.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<SqlRun?> OpenAsAsync(SqlConnection connection, SqlNamedConnection target, string password, CancellationToken cancellationToken)
    {
        using var token = WindowsCredentials.LogonNetOnly(target.Config.User!, password, out string? error);
        if (token is null)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, error!, target.Name);
        }

        DiagnosticLog.Info(SqlConfigFile.Category, SqlText.RunAsLogLine(target.Name, target.Config.User!.Trim()));
        await Task.Run(() => System.Security.Principal.WindowsIdentity.RunImpersonated(token, connection.Open), CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>Rolls back what is left of the transaction; a connection the server already dropped has nothing to roll back.</summary>
    private static async Task RollBackAsync(SqlTransaction transaction)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // The batch's own error already aborted it (XACT_ABORT), or the connection is gone.
        }
    }

    /// <summary>The server's messages, one per error, each with its number and line — what the model needs to fix the text.</summary>
    public static string Message(SqlException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex.Errors.Count == 0)
        {
            return LogText.Excerpt(ex.Message, 600);
        }

        var parts = new List<string>(ex.Errors.Count);
        foreach (SqlError error in ex.Errors)
        {
            parts.Add(SqlText.ServerError(error.Number, error.LineNumber, error.Message));
        }

        return string.Join("; ", parts.Distinct(StringComparer.Ordinal));
    }

    /// <summary>A parameter typed by its value: text as <c>nvarchar</c> (4000, or max past it), whole numbers as <c>bigint</c>, the rest as they come.</summary>
    public static SqlParameter Bind(SqlParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = "@" + value.Name;
        return value.Value switch
        {
            null => new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = DBNull.Value },
            string s => new SqlParameter(name, SqlDbType.NVarChar, s.Length <= 4000 ? 4000 : -1) { Value = s },
            long l => new SqlParameter(name, SqlDbType.BigInt) { Value = l },
            int i => new SqlParameter(name, SqlDbType.Int) { Value = i },
            decimal d => new SqlParameter(name, SqlDbType.Decimal) { Value = d, Precision = 38, Scale = d.Scale },
            double f => new SqlParameter(name, SqlDbType.Float) { Value = f },
            bool b => new SqlParameter(name, SqlDbType.Bit) { Value = b },
            var other => new SqlParameter(name, other),
        };
    }
}
