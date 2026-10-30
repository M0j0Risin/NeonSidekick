using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Office;
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
    public OracleAccess(Func<OracleCatalog> catalog) : this(catalog, null)
    {
    }

    /// <summary>An access over <paramref name="catalog"/> sharing <paramref name="names"/> (a pinned one's, <see cref="Pinned"/>), or a cache of its own.</summary>
    public OracleAccess(Func<OracleCatalog> catalog, Sql.BoundaryNames? names)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Names = names ?? new();
    }

    /// <summary>
    /// The schema a connection works in, as the dictionary spells it: the entry's own, else the user's (2026-10-09: the one it is locked
    /// to, <see cref="OracleBoundary"/>; no call names another).
    /// </summary>
    public static string Place(OracleConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return OracleIdentifier.Normalize(config.Schema) ?? OracleIdentifier.Normalize(config.User) ?? config.User?.Trim().ToUpperInvariant() ?? "";
    }

    /// <summary>The connections as the files hold them now.</summary>
    public OracleCatalog Catalog() => _catalog();

    /// <summary>The names the tools' boundary reads before a model's statement, kept a minute per connection (2026-10-09, <see cref="Sql.BoundaryNames"/>).</summary>
    public Sql.BoundaryNames Names { get; }

    /// <summary>
    /// An access over <paramref name="target"/> alone, sharing this one's names cache (2026-10-24, the database review's H11): an <c>_execute</c>
    /// call resolves its connection once and every step after — the boundary, the pane, the run — works on that entry, so a file edited in
    /// between cannot send an allowed change to another server.
    /// </summary>
    public OracleAccess Pinned(OracleNamedConnection target) => new(() => new OracleCatalog([target], []), Names);

    /// <summary>
    /// The connection a call means (<see cref="OracleCatalog.Find"/>), or null with the refusal: none defined, none named among several, or a name that
    /// matches none (the names listed).
    /// </summary>
    public OracleNamedConnection? Resolve(string? name, out SqlRun? refused)
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

    /// <summary>Whether a failed open says the password is wrong (ORA-01017): the sign-in guard holds it (2026-10-24, review K1).</summary>
    public static bool WrongPassword(Exception ex) => ex is OracleException { Number: 1017 };

    /// <summary>
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (or the only one
    /// offered when blank), in its schema (<see cref="Place"/>, 2026-10-09: the lock — the connection's own, else the user's): each one's rows
    /// read to <paramref name="maxRows"/> (<see cref="SqlGrid.ReadAsync"/>, one past to learn whether more are left), each
    /// statement bound only the parameters it names, all inside one read-only transaction that is rolled back. A timeout, a
    /// failed connect and a server error are outcomes; a cancelled token throws. The run's <see cref="SqlRun.Database"/> is the
    /// schema it worked in.
    /// </summary>
    /// <remarks><paramref name="export"/> (2026-10-29, the Office writing plan): the rows go to a workbook's sheet in place of the grid (<see cref="ExportAsync"/>).</remarks>
    public async Task<SqlRun> RunAsync(string? name, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken, SqlExportJob? export = null)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = OracleSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        string workIn = Place(target.Config);
        await using var connection = new OracleConnection(target.Config.Builder(secret.Value).ConnectionString);
        try
        {
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("oracle", target.Config.DataSource ?? "", target.Config.User?.Trim(), secret.Value, WrongPassword), connection.OpenAsync, cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.DataSource?.Trim() ?? "", OracleConfigFile.Family.Tab), target.Name);
            }
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
            if (await ProtectSessionAsync(connection, target.Name, workIn, timeoutSeconds, cancellationToken).ConfigureAwait(false) is { } unprotected)
            {
                return SqlRun.Refused(SqlOutcome.Failed, unprotected, target.Name);
            }

            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", timeoutSeconds, cancellationToken).ConfigureAwait(false);
                foreach (string sql in statements)
                {
                    // One deadline for the whole call (2026-10-24, the database review's O13: each statement had the full timeout).
                    if (SqlDeadline.Left(watch, timeoutSeconds) is not (> 0 and var left))
                    {
                        return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
                    }

                    await using var command = new OracleCommand(OracleCatalogQueries.ForServer(sql, connection.ServerVersion), connection)
                    {
                        Transaction = transaction,
                        BindByName = true,
                        CommandTimeout = left,
                        InitialLONGFetchSize = LongFetchChars,
                    };
                    var named = OracleReadOnlyGate.Binds(sql);
                    foreach (var parameter in parameters.Where(p => named.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                    {
                        command.Parameters.Add(Bind(parameter));
                    }

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    grids.Add(export is null
                        ? await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false)
                        : await export.ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false));
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
    /// its schema (<see cref="Place"/>). A session of its own — no pooling, so never
    /// one a read left <c>READ_ONLY</c> — with <c>CURRENT_SCHEMA</c> set and no transaction of the app's: ODP.NET commits the one
    /// statement as it runs (DDL commits anyway). Bound only the parameters it names. <see cref="SqlRun.Changes"/> is the rows the
    /// server counts (none for DDL or a block). A timeout (ORA-01013), a failed connect and a server error are outcomes; a cancelled
    /// token throws (the server undoes the statement).
    /// </summary>
    public async Task<SqlRun> ExecuteAsync(string? name, string sql, IReadOnlyList<SqlParameterValue> parameters, int timeoutSeconds, CancellationToken cancellationToken)
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

        var secret = OracleSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        string workIn = Place(target.Config);
        await using var connection = new OracleConnection(target.Config.Builder(secret.Value, pooling: false).ConnectionString);
        try
        {
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("oracle", target.Config.DataSource ?? "", target.Config.User?.Trim(), secret.Value, WrongPassword), connection.OpenAsync, cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.DataSource?.Trim() ?? "", OracleConfigFile.Family.Tab), target.Name);
            }
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
            if (await SetSchemaAsync(connection, workIn, timeoutSeconds, cancellationToken).ConfigureAwait(false) is { } unset)
            {
                return SqlRun.Refused(SqlOutcome.Failed, unset, target.Name);
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
    /// <remarks>
    /// Since 2026-10-24 (the database review, mechanism 7) a layer that cannot be set refuses the call: a server version that does not read
    /// (H10: it read as release 0 and the session layer was skipped with one log line), and a schema no quoted name holds (H6: the schema was
    /// left unset, and a pooled session kept the one another entry had set — the lock failed open). The schema is set from its normalized
    /// spelling as it stands (E2). Null when every layer held; else the refusal.
    /// </remarks>
    private static async Task<string?> ProtectSessionAsync(OracleConnection connection, string name, string schema, int timeoutSeconds, CancellationToken cancellationToken)
    {
        string version = connection.ServerVersion ?? "";
        int release = Release(version);
        if (release == 0)
        {
            return OracleText.VersionUnread(version);
        }

        if (release >= SessionReadOnlyRelease)
        {
            await ExecuteAsync(connection, null, "ALTER SESSION SET READ_ONLY = TRUE", timeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        else if (SessionReadOnlySkipped.TryAdd(name, 0))
        {
            DiagnosticLog.Info(OracleConfigFile.Category, OracleText.SessionReadOnlySkippedLogLine(name, version));
        }

        return await SetSchemaAsync(connection, schema, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The session's <c>CURRENT_SCHEMA</c> set to <paramref name="schema"/> (as normalized, E2), or the refusal when no quoted name holds it (H6).</summary>
    private static async Task<string?> SetSchemaAsync(OracleConnection connection, string schema, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (OracleIdentifier.QuoteNormal(schema) is not { } quoted)
        {
            return OracleText.SchemaUnset(schema);
        }

        await ExecuteAsync(connection, null, "ALTER SESSION SET CURRENT_SCHEMA = " + quoted, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        return null;
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

                    // The time zone kept (2026-10-24, the database review's B9: through GetValue a TIMESTAMP WITH TIME ZONE lost its offset,
                    // and one WITH LOCAL TIME ZONE read as a bare local time), and an INTERVAL YEAR TO MONTH as years and months, not a count.
                    case "TimeStampTZ":
                        return TimeZoned(oracle.GetOracleTimeStampTZ(ordinal));

                    case "TimeStampLTZ":
                        return TimeZoned(oracle.GetOracleTimeStampLTZ(ordinal).ToOracleTimeStampTZ());

                    case "IntervalYM":
                        return YearsMonths(oracle.GetOracleIntervalYM(ordinal));
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

    /// <summary>
    /// A query's rows written to a workbook (2026-10-29, the Office writing plan): <see cref="RunAsync"/>'s read-only session and rollback,
    /// the rows handed to <paramref name="export"/> (typed by <see cref="ExportCell"/>) instead of read into a grid.
    /// </summary>
    public Task<SqlRun> ExportAsync(string? name, string statement, IReadOnlyList<SqlParameterValue> parameters, SqlExportJob export, int maxRows, int timeoutSeconds, CancellationToken cancellationToken) =>
        RunAsync(name, [statement], parameters, maxRows, timeoutSeconds, cancellationToken, export ?? throw new ArgumentNullException(nameof(export)));

    /// <summary>
    /// An Oracle cell for a workbook (2026-10-29): a CLOB read to Excel's cap and a BLOB's, BFILE's or LONG RAW's bytes to the hex that fits,
    /// a LONG to the cap (2026-10-29, review P5-5: they went through the chat's clipped text; a LONG and a LONG RAW are fetched whole with
    /// the row, as in the chat, and only the cell is capped); a time zone's timestamp and an interval as the chat writes them (text: the
    /// offset is the value); a NUMBER past 15 digits text by its provider value; BINARY_DOUBLE and the rest by the common rules. A DATE is
    /// always a date-time, midnight too: Oracle's DATE holds a time (review P1-7: its type name "Date" read as a date alone). A LOB is read
    /// until the buffer is full or the LOB ends (review P1-8: one read may return fewer).
    /// </summary>
    public static XlsxValue ExportCell(DbDataReader reader, int ordinal, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        try
        {
            if (reader is OracleDataReader oracle && !oracle.IsDBNull(ordinal))
            {
                int fit = (XlsxValue.MaxChars - 3) / 2;
                switch (typeName)
                {
                    case "Clob" or "NClob":
                        using (var clob = oracle.GetOracleClob(ordinal))
                        {
                            var buffer = new char[(int)Math.Min(clob.Length, XlsxValue.MaxChars + 1)];
                            int read = 0;
                            while (read < buffer.Length && clob.Read(buffer, read, buffer.Length - read) is > 0 and var got)
                            {
                                read += got;
                            }

                            return XlsxValue.OfText(new string(buffer, 0, read));
                        }

                    case "Blob":
                        using (var blob = oracle.GetOracleBlob(ordinal))
                        {
                            var buffer = new byte[(int)Math.Min(blob.Length, fit)];
                            int read = blob.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                            return XlsxValue.OfBytes(buffer.AsSpan(0, read), more: blob.Length > fit);
                        }

                    case "BFile":
                        using (var file = oracle.GetOracleBFile(ordinal))
                        {
                            if (!file.IsOpen)
                            {
                                file.OpenFile();
                            }

                            var buffer = new byte[(int)Math.Min(file.Length, fit)];
                            int read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                            return XlsxValue.OfBytes(buffer.AsSpan(0, read), more: file.Length > fit);
                        }

                    case "LongRaw":
                        {
                            var buffer = new byte[fit + 1];
                            int read = (int)oracle.GetBytes(ordinal, 0, buffer, 0, buffer.Length);
                            return XlsxValue.OfBytes(buffer.AsSpan(0, Math.Min(read, fit)), more: read > fit);
                        }

                    case "Long":
                        {
                            var buffer = new char[XlsxValue.MaxChars + 1];
                            int read = (int)oracle.GetChars(ordinal, 0, buffer, 0, buffer.Length);
                            return XlsxValue.OfText(new string(buffer, 0, read));
                        }

                    case "TimeStampTZ" or "TimeStampLTZ" or "IntervalYM":
                        return XlsxValue.OfText(ReadCell(reader, ordinal));
                }
            }

            return SqlExport.Typed(reader, ordinal, typeName: null, SqlExport.InvariantText);
        }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException or OutOfMemoryException))
        {
            return XlsxValue.OfText(OracleText.Unreadable(typeName ?? reader.GetDataTypeName(ordinal)));
        }
    }

    /// <summary>A CLOB's first <see cref="SqlText.MaxCellChars"/> characters, scrubbed as every cell is and never cut inside a pair (2026-10-24, review B10, F15).</summary>
    private static string ClobText(OracleClob clob)
    {
        long length = clob.Length;
        var buffer = new char[(int)Math.Min(length, SqlText.MaxCellChars + 1)];
        int read = buffer.Length == 0 ? 0 : clob.Read(buffer, 0, buffer.Length);
        string text = Llm.Tools.UntrustedText.Scrub(Files.TextCut.Safe(new string(buffer, 0, read), SqlText.MaxCellChars));
        return length <= SqlText.MaxCellChars ? text : text + $"… ({length.ToString(CultureInfo.InvariantCulture)} chars)";
    }

    /// <summary>A time zone's timestamp with its offset, as <see cref="SqlText.Cell"/> writes a <see cref="DateTimeOffset"/> (B9).</summary>
    private static string TimeZoned(OracleTimeStampTZ value) => SqlText.Cell(new DateTimeOffset(value.Value, value.GetTimeZoneOffset()));

    /// <summary>An INTERVAL YEAR TO MONTH as Oracle writes it: <c>+1-06</c>, <c>-2-03</c> (B9).</summary>
    public static string YearsMonths(OracleIntervalYM value) => YearsMonths(value.Years, value.Months);

    /// <summary>Years and months as Oracle writes an INTERVAL YEAR TO MONTH. Pure.</summary>
    public static string YearsMonths(int years, int months)
    {
        bool negative = years < 0 || months < 0;
        return (negative ? "-" : "+") + Math.Abs(years).ToString(CultureInfo.InvariantCulture) + "-" + Math.Abs(months).ToString("00", CultureInfo.InvariantCulture);
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
    /// A parameter typed by its value, bound by name (<c>:name</c>): text as <c>VARCHAR2</c> (a <c>CLOB</c> past 4000 bytes as UTF-8 — the
    /// limit is in bytes, so 4,000 multibyte characters failed with ORA-01461; 2026-10-24, the database review's E10), whole numbers as
    /// <c>NUMBER</c> through <see cref="long"/>, decimals exact, floats as <c>BINARY_DOUBLE</c>, a boolean as 1 or 0 (what a <c>NUMBER(1)</c>
    /// flag holds, and 23ai's BOOLEAN takes).
    /// </summary>
    public static OracleParameter Bind(SqlParameterValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string name = value.Name;
        return value.Value switch
        {
            null => new OracleParameter(name, OracleDbType.Varchar2) { Value = DBNull.Value },
            string s => new OracleParameter(name, System.Text.Encoding.UTF8.GetByteCount(s) <= 4000 ? OracleDbType.Varchar2 : OracleDbType.Clob) { Value = s },
            long l => new OracleParameter(name, OracleDbType.Int64) { Value = l },
            int i => new OracleParameter(name, OracleDbType.Int32) { Value = i },
            decimal d => new OracleParameter(name, OracleDbType.Decimal) { Value = d },
            double f => new OracleParameter(name, OracleDbType.BinaryDouble) { Value = f },
            bool b => new OracleParameter(name, OracleDbType.Int32) { Value = b ? 1 : 0 },
            var other => new OracleParameter(name, other),
        };
    }
}
