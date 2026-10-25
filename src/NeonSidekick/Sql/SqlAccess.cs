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

    /// <summary>A change asked of a connection whose entry does not say <c>"access": "readwrite"</c> (2026-10-05; the server families' <c>_execute</c> tools).</summary>
    ReadOnlyConnection,
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

    /// <summary>A text cell's first <see cref="SqlText.MaxCellChars"/> characters, and whether there were more (E6).</summary>
    private static string BoundedChars(SqlDataReader reader, int ordinal)
    {
        var buffer = new char[SqlText.MaxCellChars + 1];
        int read = (int)reader.GetChars(ordinal, 0, buffer, 0, buffer.Length);
        return read <= SqlText.MaxCellChars
            ? SqlText.Cell(new string(buffer, 0, read))
            : SqlText.Cell(new string(buffer, 0, SqlText.MaxCellChars)) + SqlText.MoreChars(SqlText.MaxCellChars);
    }

    /// <summary>A binary cell's first <see cref="SqlText.MaxBinaryBytes"/> bytes, and whether there were more (E6).</summary>
    private static string BoundedBytes(SqlDataReader reader, int ordinal)
    {
        var buffer = new byte[SqlText.MaxBinaryBytes + 1];
        int read = (int)reader.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
        return read <= SqlText.MaxBinaryBytes
            ? SqlText.Cell(buffer[..read])
            : SqlText.Cell(buffer[..SqlText.MaxBinaryBytes]) + SqlText.MoreBytes(SqlText.MaxBinaryBytes);
    }

    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return SqlText.Null;
            }

            // SQL Server's reader runs with SequentialAccess (2026-10-24, the database review's E6): a text or binary cell is read only as far
            // as the answer shows it, never whole (REPLICATE or a varbinary(max) of gigabytes was loaded into memory before the 400-character
            // clip), and a decimal through its provider value, which keeps every digit (a sequential column cannot be read again after a
            // failed GetValue).
            if (reader is SqlDataReader sql)
            {
                var type = sql.GetFieldType(ordinal);
                if (type == typeof(string))
                {
                    return BoundedChars(sql, ordinal);
                }

                if (type == typeof(byte[]))
                {
                    return BoundedBytes(sql, ordinal);
                }

                if (type == typeof(decimal))
                {
                    return SqlText.Cell(sql.GetProviderSpecificValue(ordinal) switch
                    {
                        System.Data.SqlTypes.SqlDecimal d => d.ToString(),
                        System.Data.SqlTypes.SqlMoney m => m.Value,
                        var other => other,
                    });
                }
            }

            return SqlText.Cell(reader.GetValue(ordinal));
        }
        catch (OverflowException) when (reader is not SqlDataReader)
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
    public SqlAccess(Func<SqlCatalog> catalog) : this(catalog, null)
    {
    }

    /// <summary>An access over <paramref name="catalog"/> sharing <paramref name="names"/> (a pinned one's, <see cref="Pinned"/>), or a cache of its own.</summary>
    public SqlAccess(Func<SqlCatalog> catalog, BoundaryNames? names)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Names = names ?? new();
    }

    /// <summary>The connections as the files hold them now.</summary>
    public SqlCatalog Catalog() => _catalog();

    /// <summary>The names the tools' boundary reads before a model's statement, kept a minute per connection (2026-10-09, <see cref="BoundaryNames"/>).</summary>
    public BoundaryNames Names { get; }

    /// <summary>
    /// An access over <paramref name="target"/> alone, sharing this one's names cache (2026-10-24, the database review's H11): an <c>_execute</c>
    /// call resolves its connection once and every step after — the boundary, the pane, the run — works on that entry, so a file edited in
    /// between cannot send an allowed change to another server.
    /// </summary>
    public SqlAccess Pinned(SqlNamedConnection target) => new(() => new SqlCatalog([target], []), Names);

    /// <summary>
    /// The connection a call means (<see cref="SqlCatalog.Find"/>), or null with the <c>Error:</c> sentence:
    /// none defined, none named among several, or a name that matches none (the names listed).
    /// </summary>
    public SqlNamedConnection? Resolve(string? name, out SqlRun? refused)
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

    /// <summary>
    /// Runs <paramref name="sql"/> on the connection <paramref name="name"/> names (or the only one
    /// offered when blank), in the connection's own database (2026-10-09: the one it reaches; a server that opens any other — a
    /// login's default — is refused before the batch): every result set read to
    /// <paramref name="maxRows"/> (<see cref="SqlGrid.ReadAsync"/>), the command cancelled at the first set that has
    /// more — so the server stops, and the reader never drains a million rows on dispose — and the transaction
    /// rolled back. A timeout, a failed connect and a server error are outcomes; a cancelled token throws.
    /// </summary>
    /// <remarks>
    /// <paramref name="keepLaterSets"/> (2026-10-24, the database review's O1) is the app's catalog batches': a result set past the cap moves
    /// on to the next instead of cancelling the batch, so <c>sql_describe</c>'s indexes, checks, triggers and description are never lost
    /// behind a table with more columns than the cap (they were reported as none). A model's query keeps the cancel. The reader runs with
    /// SequentialAccess (E6).
    /// </remarks>
    public async Task<SqlRun> RunAsync(string? name, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken, bool keepLaterSets = false)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = SqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(secret.Value);
        string catalogName = builder.InitialCatalog;
        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            if (await OpenGuardedAsync(connection, target, secret.Value!, cancellationToken).ConfigureAwait(false) is { } refusedOpen)
            {
                return refusedOpen;
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException && cancellationToken.IsCancellationRequested)
        {
            // A runas open runs to its end whatever Esc does (the token never reaches it); its failure after Esc is the cancel (2026-10-24, the
            // database review's O11: it escaped both catches below, filtered on a token not cancelled, as a raw SqlException).
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(SqlConfigFile.Category, SqlText.ConnectFailedLogLine(target.Name, catalogName, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when ((ex is InvalidOperationException or ArgumentException || IsUnsupportedHere(ex)) && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        string databaseName = connection.Database;
        if (!string.Equals(databaseName, catalogName, StringComparison.OrdinalIgnoreCase))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqlText.WrongDatabase(catalogName, databaseName), target.Name);
        }

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

                await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
                {
                    do
                    {
                        if (reader.FieldCount == 0)
                        {
                            continue;
                        }

                        var grid = await SqlGrid.ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
                        grids.Add(grid);
                        if (grid.More && !keepLaterSets)
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
    /// <c>sql_execute</c>'s run (2026-10-05, <c>SQL mode</c> <c>read-write</c>): one statement the write gate passed, on a connection
    /// whose entry says <c>"access": "readwrite"</c> (any other is <see cref="SqlOutcome.ReadOnlyConnection"/>, whatever the caller
    /// checked), in the connection's own database, with read-write intent (<see cref="SqlConnectionConfig.Builder"/>) and the
    /// <c>runas</c> sign-in as for a read. No transaction of the app's: SQL Server commits the one statement on its own, atomically (a
    /// procedure's own transactions are its own). Every result set (an <c>OUTPUT</c> clause's rows, a procedure's) is read to
    /// <paramref name="maxRows"/> and the rest drained, never cancelled — a cancel would undo the statement. <see cref="SqlRun.Changes"/>
    /// is the rows the server counts. A timeout, a failed connect and a server error are outcomes; a cancelled token throws (the
    /// server undoes the statement).
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

        var secret = SqlSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(secret.Value, readOnlyIntent: false);
        string catalogName = builder.InitialCatalog;
        await using var connection = new SqlConnection(builder.ConnectionString);
        try
        {
            if (await OpenGuardedAsync(connection, target, secret.Value!, cancellationToken).ConfigureAwait(false) is { } refusedOpen)
            {
                return refusedOpen;
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException && cancellationToken.IsCancellationRequested)
        {
            // A runas open runs to its end whatever Esc does (the token never reaches it); its failure after Esc is the cancel (2026-10-24, the
            // database review's O11: it escaped both catches below, filtered on a token not cancelled, as a raw SqlException).
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(SqlConfigFile.Category, SqlText.ConnectFailedLogLine(target.Name, catalogName, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when ((ex is InvalidOperationException or ArgumentException || IsUnsupportedHere(ex)) && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        string databaseName = connection.Database;
        if (!string.Equals(databaseName, catalogName, StringComparison.OrdinalIgnoreCase))
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqlText.WrongDatabase(catalogName, databaseName), target.Name);
        }

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        int? changes;
        try
        {
            // Implicit transactions off here, never the server's or the login's default trusted (2026-10-24, the database review's H4: under
            // IMPLICIT_TRANSACTIONS ON the change said it was made and was audited, then the pool's reset rolled it back, its locks held meanwhile).
            await using (var session = new SqlCommand(ImplicitTransactionsOff, connection) { CommandTimeout = timeoutSeconds })
            {
                await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            foreach (var parameter in parameters)
            {
                command.Parameters.Add(Bind(parameter));
            }

            // The statement's own count is its last one (2026-10-24, review I13: RecordsAffected summed a trigger's rows too, where the trigger
            // did not SET NOCOUNT ON, so the answer and the audit overstated the change): a trigger's counts come before its statement's.
            int? last = null;
            command.StatementCompleted += (_, e) => last = e.RecordCount;

            await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
            {
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        grids.Add(await SqlGrid.ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false));
                    }
                }
                while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

                await reader.CloseAsync().ConfigureAwait(false);
                changes = last ?? (reader.RecordsAffected >= 0 ? reader.RecordsAffected : null);
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
        catch (SqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, databaseName, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, databaseName, grids, watch.Elapsed) { Changes = changes };
    }

    /// <summary>What a change's session runs before its statement, so the statement commits on its own (H4).</summary>
    public const string ImplicitTransactionsOff = "SET IMPLICIT_TRANSACTIONS OFF";

    /// <summary>
    /// Opens <paramref name="connection"/> under the sign-in guard (2026-10-24, the database review's K1, <see cref="DatabaseSignIn"/>): a SQL
    /// login's or a <c>runas</c> account's password refused before (18456, login failed) is not sent again until it changes. Null when open;
    /// else the refusal. A Windows sign-in as the app's own account sends no password and is never held.
    /// </summary>
    private static async Task<SqlRun?> OpenGuardedAsync(SqlConnection connection, SqlNamedConnection target, string password, CancellationToken cancellationToken)
    {
        if (target.Config.IsRunAs && !OperatingSystem.IsWindows() && !KerberosSignIn.Offered)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, WindowsCredentials.NotWindows, target.Name);
        }

        // A runas sign-in the app refuses itself (the Windows logon, the Mac's Kerberos step) is a failed sign-in to the guard too (2026-10-24, the
        // review of these fixes: answered as a run, it counted as proven, and the wrong password went to the KDC at every call).
        var call = DatabaseSignIn.Call("sql", target.Config.Server ?? "", target.Config.IsWindows ? null : target.Config.User?.Trim(), password,
            ex => ex is SqlException { Number: 18456 } or RefusedSignIn);
        try
        {
            if (await DatabaseSignIn.OpenAsync(call, async token =>
            {
                if (target.Config.IsRunAs)
                {
                    if (await OpenAsAsync(connection, target, password, token).ConfigureAwait(false) is { } refused)
                    {
                        throw new RefusedSignIn(refused);
                    }
                }
                else
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                }
            }, cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.Server?.Trim() ?? "", SqlText.SqlMenuPath), target.Name);
            }
        }
        catch (RefusedSignIn refused)
        {
            return refused.Run;
        }

        return null;
    }

    /// <summary>A runas sign-in the app refused, carried through the guard as a failure (<see cref="OpenGuardedAsync"/>).</summary>
    private sealed class RefusedSignIn(SqlRun run) : Exception(run.Detail)
    {
        public SqlRun Run { get; } = run;
    }

    /// <summary>
    /// Opens <paramref name="connection"/> signed in as the <c>runas</c> account: Windows' netonly token
    /// (<see cref="OpenNetOnlyAsync"/>), or on a Mac (2026-10-11) the account's own Kerberos sign-in (<see cref="OpenKerberosAsync"/>).
    /// Null when open; else the refusal.
    /// </summary>
    private static Task<SqlRun?> OpenAsAsync(SqlConnection connection, SqlNamedConnection target, string password, CancellationToken cancellationToken) =>
        OperatingSystem.IsWindows()
            ? OpenNetOnlyAsync(connection, target, password, cancellationToken)
            : OpenKerberosAsync(connection, target, password, cancellationToken);

    /// <summary>
    /// <see cref="OpenAsAsync"/> on a Mac (2026-10-11): the account as <c>name@REALM</c> (<see cref="KerberosSignIn.Principal"/>; a
    /// <c>DOMAIN\name</c> that Windows takes is refused here with the form to use, at the connect rather than the file's load, so a
    /// <c>sql.json</c> shared with a Windows PC keeps its entry), a <see cref="KerberosSignIn"/> as the connection's SSPI provider, and
    /// the open. Like the Windows path, ESC waits the open out (the connect timeout bounds it): cancelling it mid-handshake would
    /// dispose the provider under SqlClient still calling it. A failed Kerberos step is said as itself, the status as .NET names it
    /// — not SqlClient's "target principal name is incorrect", which reads as a trust or SPN problem; the status alone does not
    /// tell a wrong password from an account the server refuses (one that got its service ticket still failed), so the sentence
    /// claims neither. Any other failure (the server's own login refusal) goes to the caller's catch as every connect's does.
    /// The <c>using</c> lets go of a handshake the open left half-finished; the connection keeps the provider for the batch, and a
    /// reconnect in it (<c>ConnectRetryCount</c>) signs in afresh through it (code review of 522af7e, <see cref="KerberosSignIn"/>).
    /// </summary>
    private static async Task<SqlRun?> OpenKerberosAsync(SqlConnection connection, SqlNamedConnection target, string password, CancellationToken cancellationToken)
    {
        string account = target.Config.User?.Trim() ?? "";
        if (KerberosSignIn.Principal(account) is not { } principal)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqlText.RunAsNeedsRealm(account), target.Name);
        }

        using var signIn = new KerberosSignIn(principal, password);
        connection.SspiContextProvider = signIn;
        DiagnosticLog.Info(SqlConfigFile.Category, SqlText.RunAsLogLine(target.Name, principal));
        try
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException ex) when (signIn.Failure is { } status)
        {
            DiagnosticLog.Warn(SqlConfigFile.Category, SqlText.KerberosFailedLogLine(target.Name, principal, status.ToString(), LogText.Excerpt(ex.Message)));
            cancellationToken.ThrowIfCancellationRequested();
            return SqlRun.Refused(SqlOutcome.ConnectFailed, SqlText.KerberosFailed(principal, status.ToString()), target.Name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>
    /// <see cref="OpenAsAsync"/> on Windows (later on 2026-09-23): a
    /// <c>NEW_CREDENTIALS</c> token (<see cref="WindowsCredentials.LogonNetOnly"/>), and the <b>synchronous</b> open run
    /// impersonated on a worker thread — the sign-in's SSPI handshake takes the calling thread's token, and a sync open
    /// keeps the whole handshake on the one thread that holds it (the async open's continuations may land on others).
    /// The connect timeout bounds it, and ESC waits it out: abandoning the open would dispose the token and the connection under the thread still using them. Only the sign-in is impersonated: once open, the
    /// batch runs as every other.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<SqlRun?> OpenNetOnlyAsync(SqlConnection connection, SqlNamedConnection target, string password, CancellationToken cancellationToken)
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

    /// <summary>
    /// A server spelling this platform's SqlClient cannot reach at all (2026-10-06, the macOS build): an <c>np:\\.\pipe\…</c>
    /// named pipe throws <see cref="PlatformNotSupportedException"/> from the open off Windows, which escaped the tool as a throw
    /// rather than the connect error every other unreachable server is. Off Windows only, so Windows' handling is unchanged.
    /// </summary>
    private static bool IsUnsupportedHere(Exception ex) => ex is PlatformNotSupportedException && !OperatingSystem.IsWindows();

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
