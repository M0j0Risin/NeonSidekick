using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace NeonSidekick.Oracle;

/// <summary>
/// The Oracle tools' one door to Oracle (2026-09-30), <see cref="SqlAccess"/>'s shape: built once per app, the connections
/// read afresh from <c>oracle.json</c> at every call, a connection opened per call and closed after (ODP.NET pools the
/// session), never held across calls. Read-only is the user's first ask of it ("make every effort"), so every call stands
/// behind three layers of the server's own under the tools' <see cref="OracleReadOnlyGate"/>:
/// <list type="number">
/// <item>the session: <c>ALTER SESSION SET READ_ONLY = TRUE</c> on 23ai and later (ORA-28193 on any DML or DDL — the spike
/// saw it refuse an autonomous-transaction function and <c>WITH FUNCTION</c> too); an older server has none, and says so once
/// in the log;</item>
/// <item>the transaction: <c>SET TRANSACTION READ ONLY</c> as its first statement (ORA-01456 on DML and <c>FOR UPDATE</c>, even
/// inside a called function);</item>
/// <item>the rollback: whatever ran, the transaction is rolled back, never committed.</item>
/// </list>
/// The account is the fourth (<see cref="OracleConnectionConfig.Problem"/> refuses SYS; the wizard warns of one that can
/// write). ODP.NET runs one statement per command, so a call is a list of statements, one result set each, all in the one
/// read-only transaction. Each call sets its schema (<c>CURRENT_SCHEMA</c>, quoted by the app, never the model's text) since
/// a pooled session keeps the last. Async all the way, the turn's token passed to every call, so ESC cancels a running
/// query on the server. The password never leaves the connection string: an error is the server's message, and the log
/// names the connection, never the string.
/// </summary>
public sealed class OracleAccess
{
    /// <summary>The rows a catalog listing (tables, relationships, …) shows at most.</summary>
    public const int MaxCatalogRows = 1000;

    /// <summary>
    /// How much of a <c>LONG</c> column is fetched with its row (the dictionary's <c>DATA_DEFAULT</c>, <c>SEARCH_CONDITION</c>,
    /// <c>ALL_VIEWS.TEXT</c>): -1, all of it. A fixed size reads a longer value as NULL unless the query selects the ROWID
    /// too (<c>ALL_OBJECTS</c>' 16,423-character text did, against Oracle Free 23.26); a dictionary text is kilobytes, and a
    /// cell shows <see cref="SqlText.MaxCellChars"/> of it anyway.
    /// </summary>
    public const int LongFetchChars = -1;

    /// <summary>The first release with the session parameter <c>READ_ONLY</c> (23ai).</summary>
    public const int SessionReadOnlyRelease = 23;

    /// <summary>The server errors a cancel or a timeout answers with: ORA-01013, user requested cancel.</summary>
    public const int CancelledErrorNumber = 1013;

    private static readonly ConcurrentDictionary<string, byte> SessionReadOnlySkipped = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<OracleCatalog> _catalog;

    /// <param name="catalog">The connections in force; the app's reads the profile's and the home's <c>oracle.json</c> at every call.</param>
    public OracleAccess(Func<OracleCatalog> catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>The connections as the files hold them now.</summary>
    public OracleCatalog Catalog() => _catalog();

    /// <summary>
    /// The connection a call means (<see cref="OracleCatalog.Find"/>), or null with the refusal: none defined, or a name that
    /// matches none (the names listed).
    /// </summary>
    public OracleNamedConnection? Resolve(string? name, string? defaultName, out SqlRun? refused)
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
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (<paramref name="defaultName"/> or the
    /// first when blank), in <paramref name="schema"/> when given (else the connection's own, else the user's): each one's rows
    /// read to <paramref name="maxRows"/> (<see cref="SqlGrid.ReadAsync"/>, one past to learn whether more are left), each
    /// statement bound only the parameters it names, all inside one read-only transaction that is rolled back. A timeout, a
    /// failed connect and a server error are outcomes; a cancelled token throws. The run's <see cref="SqlRun.Database"/> is the
    /// schema it worked in.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? name, string? defaultName, string? schema, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = OracleSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        string workIn = OracleIdentifier.Normalize(schema) ?? OracleIdentifier.Normalize(target.Config.Schema) ?? OracleIdentifier.Normalize(target.Config.User) ?? target.Config.User!.Trim().ToUpperInvariant();
        await using var connection = new OracleConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(OracleConfigFile.Category, OracleText.ConnectFailedLogLine(target.Name, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
        try
        {
            await ProtectSessionAsync(connection, target.Name, workIn, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", timeoutSeconds, cancellationToken).ConfigureAwait(false);
                foreach (string sql in statements)
                {
                    await using var command = new OracleCommand(sql, connection)
                    {
                        Transaction = transaction,
                        BindByName = true,
                        CommandTimeout = timeoutSeconds,
                        InitialLONGFetchSize = LongFetchChars,
                    };
                    var named = OracleReadOnlyGate.Binds(sql);
                    foreach (var parameter in parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        command.Parameters.Add(Bind(parameter));
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    grids.Add(await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false));
                }
            }
            finally
            {
                RollBack(transaction);
            }
        }
        catch (OracleException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == CancelledErrorNumber)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is OracleException { Number: CancelledErrorNumber })
        {
            // ODP.NET's async path answers its own CommandTimeout this way (found against Oracle Free 23.26): the token never
            // fired, so it is the timeout, not ESC.
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (OracleException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed);
    }

    /// <summary>
    /// <c>oracle_execute</c>'s run (2026-10-05, <c>Oracle mode</c> <c>read-write</c>): one statement the write gate passed (a PL/SQL
    /// unit with its final <c>;</c>), on a connection whose entry says <c>"access": "readwrite"</c> (any other is
    /// <see cref="SqlOutcome.ReadOnlyConnection"/>, whatever the caller checked; SYS stays refused by the entry's own problem), in
    /// <paramref name="schema"/> when given (else the connection's own, else the user's). A session of its own — no pooling, so never
    /// one a read left <c>READ_ONLY</c> — with <c>CURRENT_SCHEMA</c> set and no transaction of the app's: ODP.NET commits the one
    /// statement as it runs (DDL commits anyway). Bound only the parameters it names. <see cref="SqlRun.Changes"/> is the rows the
    /// server counts (none for DDL or a block). A timeout (ORA-01013), a failed connect and a server error are outcomes; a cancelled
    /// token throws (the server undoes the statement).
    /// </summary>
    public async Task<SqlRun> ExecuteAsync(string? name, string? defaultName, string? schema, string sql, IReadOnlyList<SqlParameterValue> parameters, int timeoutSeconds, CancellationToken cancellationToken)
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

        var secret = OracleSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        string workIn = OracleIdentifier.Normalize(schema) ?? OracleIdentifier.Normalize(target.Config.Schema) ?? OracleIdentifier.Normalize(target.Config.User) ?? target.Config.User!.Trim().ToUpperInvariant();
        await using var connection = new OracleConnection(target.Config.Builder(secret.Value, pooling: false).ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException ex) when (!cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warn(OracleConfigFile.Category, OracleText.ConnectFailedLogLine(target.Name, ex.Number, LogText.Excerpt(ex.Message)));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, Message(ex), target.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException && !cancellationToken.IsCancellationRequested)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, LogText.Excerpt(ex.Message), target.Name);
        }

        var watch = Stopwatch.StartNew();
        int? changes;
        try
        {
            if (OracleIdentifier.Quote(workIn) is { } quoted)
            {
                await ExecuteAsync(connection, null, "ALTER SESSION SET CURRENT_SCHEMA = " + quoted, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            }

            await using var command = new OracleCommand(sql, connection) { BindByName = true, CommandTimeout = timeoutSeconds };
            var named = OracleReadOnlyGate.Binds(sql);
            foreach (var parameter in parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            {
                command.Parameters.Add(Bind(parameter));
            }

            int count = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            changes = count >= 0 ? count : null;
        }
        catch (OracleException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (OracleException ex) when (ex.Number == CancelledErrorNumber)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, [], watch.Elapsed);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is OracleException { Number: CancelledErrorNumber })
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, [], watch.Elapsed);
        }
        catch (OracleException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex), target.Name, workIn, [], watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, [], watch.Elapsed) { Changes = changes };
    }

    /// <summary>
    /// The session's layers, the app's own statements before any transaction: <c>READ_ONLY</c> on 23ai and later (skipped
    /// before, logged once per connection), then the schema this call works in.
    /// </summary>
    private static async Task ProtectSessionAsync(OracleConnection connection, string name, string schema, int timeoutSeconds, CancellationToken cancellationToken)
    {
        string version = connection.ServerVersion ?? "";
        if (Release(version) >= SessionReadOnlyRelease)
        {
            await ExecuteAsync(connection, null, "ALTER SESSION SET READ_ONLY = TRUE", timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        else if (SessionReadOnlySkipped.TryAdd(name, 0))
        {
            DiagnosticLog.Info(OracleConfigFile.Category, OracleText.SessionReadOnlySkippedLogLine(name, version));
        }

        // A user name that is no plain identifier (it was quoted when made) leaves the session where it signed in.
        if (OracleIdentifier.Quote(schema) is { } quoted)
        {
            await ExecuteAsync(connection, null, "ALTER SESSION SET CURRENT_SCHEMA = " + quoted, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The major release of a server version (<c>23.26.3.0.0</c> is 23); 0 when it does not read.</summary>
    public static int Release(string? version) =>
        int.TryParse((version ?? "").Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major) ? major : 0;

    private static async Task ExecuteAsync(OracleConnection connection, OracleTransaction? transaction, string sql, int timeoutSeconds, CancellationToken cancellationToken)
    {
        await using var command = new OracleCommand(sql, connection) { Transaction = transaction, CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back what is left of the transaction; a connection the server already dropped has nothing to roll back.</summary>
    private static void RollBack(OracleTransaction transaction)
    {
        try
        {
            if (transaction.Connection is not null)
            {
                transaction.Rollback();
            }
        }
        catch (Exception ex) when (ex is OracleException or InvalidOperationException)
        {
            // The connection is gone, or the transaction already ended with the error.
        }
    }

    /// <summary>
    /// One cell as text: a CLOB's first <see cref="SqlText.MaxCellChars"/> characters and a BLOB's first
    /// <see cref="SqlText.MaxBinaryBytes"/> bytes read, never the whole LOB; a <c>NUMBER</c> past <see cref="decimal"/>
    /// (38 digits, or an exponent past 28 places) as its <see cref="OracleDecimal"/> text, which keeps every digit; a type the
    /// client cannot read (an object type, <c>SDO_GEOMETRY</c>, <c>XMLTYPE</c>) as <see cref="OracleText.Unreadable"/>.
    /// </summary>
    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return SqlText.Null;
            }

            if (reader is OracleDataReader oracle)
            {
                switch (oracle.GetDataTypeName(ordinal))
                {
                    case "Clob" or "NClob":
                        using (var clob = oracle.GetOracleClob(ordinal))
                        {
                            return ClobText(clob);
                        }

                    case "Blob":
                        using (var blob = oracle.GetOracleBlob(ordinal))
                        {
                            return BlobText(blob);
                        }
                }
            }

            return SqlText.Cell(reader.GetValue(ordinal));
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            try
            {
                return SqlText.Cell(reader.GetProviderSpecificValue(ordinal)?.ToString() ?? "");
            }
            catch (Exception inner) when (inner is not (DbException or OperationCanceledException or OutOfMemoryException))
            {
                return OracleText.Unreadable(reader.GetDataTypeName(ordinal));
            }
        }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            return OracleText.Unreadable(reader.GetDataTypeName(ordinal));
        }
    }

    private static string ClobText(OracleClob clob)
    {
        long length = clob.Length;
        var buffer = new char[(int)Math.Min(length, SqlText.MaxCellChars)];
        int read = buffer.Length == 0 ? 0 : clob.Read(buffer, 0, buffer.Length);
        string text = new(buffer, 0, read);
        return length <= SqlText.MaxCellChars ? text : text + $"… ({length.ToString(CultureInfo.InvariantCulture)} chars)";
    }

    private static string BlobText(OracleBlob blob)
    {
        long length = blob.Length;
        var buffer = new byte[(int)Math.Min(length, SqlText.MaxBinaryBytes)];
        int read = buffer.Length == 0 ? 0 : blob.Read(buffer, 0, buffer.Length);
        string hex = "0x" + Convert.ToHexString(buffer, 0, read);
        return length <= SqlText.MaxBinaryBytes ? hex : hex + $"… ({length.ToString(CultureInfo.InvariantCulture)} bytes)";
    }

    /// <summary>
    /// The server's messages, one per error, the help links (a <c>Help:</c> line, or 23ai's bare
    /// <c>https://docs.oracle.com/error-help/…</c>) dropped — <c>ORA-00942: table or view "HR"."X" does not exist</c> is what
    /// the model needs to fix the text.
    /// </summary>
    public static string Message(OracleException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var parts = new List<string>();
        foreach (OracleError error in ex.Errors)
        {
            parts.Add(Clean(error.Message));
        }

        if (parts.Count == 0)
        {
            parts.Add(Clean(ex.Message));
        }

        return LogText.Excerpt(string.Join("; ", parts.Where(p => p.Length > 0).Distinct(StringComparer.Ordinal)), 600);
    }

    private static string Clean(string? message) =>
        string.Join(" ", (message ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Help:", StringComparison.Ordinal) && !l.StartsWith("https://", StringComparison.Ordinal)));

    /// <summary>
    /// A parameter typed by its value, bound by name (<c>:name</c>): text as <c>VARCHAR2</c> (a <c>CLOB</c> past 4000
    /// characters), whole numbers as <c>NUMBER</c> through <see cref="long"/>, decimals exact, floats as
    /// <c>BINARY_DOUBLE</c>, a boolean as 1 or 0 (what a <c>NUMBER(1)</c> flag holds, and 23ai's BOOLEAN takes).
    /// </summary>
    public static OracleParameter Bind(SqlParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = value.Name;
        return value.Value switch
        {
            null => new OracleParameter(name, OracleDbType.Varchar2) { Value = DBNull.Value },
            string s => new OracleParameter(name, s.Length <= 4000 ? OracleDbType.Varchar2 : OracleDbType.Clob) { Value = s },
            long l => new OracleParameter(name, OracleDbType.Int64) { Value = l },
            int i => new OracleParameter(name, OracleDbType.Int32) { Value = i },
            decimal d => new OracleParameter(name, OracleDbType.Decimal) { Value = d },
            double f => new OracleParameter(name, OracleDbType.BinaryDouble) { Value = f },
            bool b => new OracleParameter(name, OracleDbType.Int32) { Value = b ? 1 : 0 },
            var other => new OracleParameter(name, other),
        };
    }
}
