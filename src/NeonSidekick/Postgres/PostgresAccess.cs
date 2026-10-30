using System.Buffers.Binary;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;
using NeonSidekick.Office;
using NeonSidekick.Sql;
using Npgsql;
using NpgsqlTypes;

namespace NeonSidekick.Postgres;

/// <summary>
/// The PostgreSQL tools' one door (2026-10-04), <c>MySqlAccess</c>'s shape over Npgsql: built once per app, the connections read afresh
/// from <c>postgres.json</c> at every call, a data source built per call through <see cref="NpgsqlSlimDataSourceBuilder"/> (the
/// NativeAOT-clean builder, opted into the type handlers a read meets) and disposed after, so nothing is held between calls. Read-only,
/// under the tools' <see cref="PostgresReadOnlyGate"/>:
/// <list type="number">
/// <item>the session: <c>default_transaction_read_only</c>, <c>statement_timeout</c> and <c>lock_timeout</c> set at startup
/// (<see cref="PostgresConnectionConfig.Builder"/>), so the server stops a long statement and refuses a write on its own;</item>
/// <item>the transaction: <c>REPEATABLE READ</c>, <c>SET TRANSACTION READ ONLY</c>, always rolled back (Postgres then refuses every write,
/// <c>nextval</c> and a temporary table with SQLSTATE 25006);</item>
/// <item>the account last: the wizard warns of a superuser or a role with write grants (the user's call: a dev container signs in as
/// <c>postgres</c>, so it warns rather than refuses).</item>
/// </list>
/// A call is a list of statements, one result set each, all in the one transaction, in the connection's own database alone (2026-10-09:
/// the per-call one went with the lock). The password never leaves the connection string.
/// </summary>
public sealed class PostgresAccess
{
    public const int MaxCatalogRows = 1000;

    /// <summary>SQLSTATE of a statement <c>statement_timeout</c> (or a cancel) stopped.</summary>
    public const string QueryCanceled = "57014";

    /// <summary>SQLSTATE of a write a read-only transaction refused.</summary>
    public const string ReadOnlyTransaction = "25006";

    /// <summary>SQLSTATE of a function or operator that does not exist (<c>=@</c> from an unbound <c>id=@id</c>).</summary>
    public const string UndefinedFunction = "42883";

    /// <summary>SQLSTATE of a syntax error.</summary>
    public const string SyntaxError = "42601";

    private readonly Func<PostgresCatalog> _catalog;

    public PostgresAccess(Func<PostgresCatalog> catalog) : this(catalog, null)
    {
    }

    /// <summary>An access over <paramref name="catalog"/> sharing <paramref name="names"/> (a pinned one's, <see cref="Pinned"/>), or a cache of its own.</summary>
    public PostgresAccess(Func<PostgresCatalog> catalog, Sql.BoundaryNames? names)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Names = names ?? new();
    }

    public PostgresCatalog Catalog() => _catalog();

    /// <summary>The names the tools' boundary reads before a model's statement, kept a minute per connection (2026-10-09, <see cref="Sql.BoundaryNames"/>).</summary>
    public Sql.BoundaryNames Names { get; }

    /// <summary>
    /// An access over <paramref name="target"/> alone, sharing this one's names cache (2026-10-24, the database review's H11): an <c>_execute</c>
    /// call resolves its connection once and every step after — the boundary, the pane, the run — works on that entry, so a file edited in
    /// between cannot send an allowed change to another server.
    /// </summary>
    public PostgresAccess Pinned(PostgresNamedConnection target) => new(() => new PostgresCatalog([target], []), Names);

    public PostgresNamedConnection? Resolve(string? name, out SqlRun? refused)
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
    /// Why the session's settings did not hold, or null: the read-only default (for a read), <c>standard_conforming_strings</c>, a statement
    /// timeout, and under a schema lock the search path being that schema alone (H1).
    /// </summary>
    private static async Task<string?> SessionRefusalAsync(NpgsqlConnection connection, PostgresConnectionConfig config, bool readOnly, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(SessionCheck, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PostgresText.SessionNotSet("the settings");
            }

            string? schema = string.IsNullOrWhiteSpace(config.Schema) ? null : config.Schema.Trim();
            return PostgresText.SessionProblem(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), readOnly, schema);
        }
        catch (NpgsqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A read-back the server or a pooler refuses is a refusal, never a raw throw (2026-10-24, the review of these fixes: it ran outside the
            // callers' catches, and postgres_execute's attempt then had no audit line).
            return PostgresText.SessionNotSet("the settings") + " (" + Diagnostics.LogText.Excerpt(ex.Message) + ")";
        }
    }

    /// <summary>The settings <see cref="SessionRefusalAsync"/> reads, in its order.</summary>
    public const string SessionCheck =
        "SELECT current_setting('default_transaction_read_only'), current_setting('standard_conforming_strings'), current_setting('statement_timeout'), array_to_string(current_schemas(false), ',')";

    /// <summary>The connections whose unchecked TLS was noted this run (2026-10-24, decision DB1: once a run each, in the log).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_tlsNoted = new(StringComparer.OrdinalIgnoreCase);

    private static void NoteUncheckedTls(string name, bool checkedTls)
    {
        if (!checkedTls && s_tlsNoted.TryAdd(name, 0))
        {
            DiagnosticLog.Warn(PostgresConfigFile.Category, Sql.SqlText.TlsUncheckedLogLine("PostgreSQL", name));
        }
    }

    /// <summary>Whether a failed open says the password is wrong (28P01, invalid_password): the sign-in guard holds it (2026-10-24, review K1).</summary>
    public static bool WrongPassword(Exception ex) => ex is PostgresException { SqlState: "28P01" };

    /// <summary>The data source for one connection string: slim, with the handlers a read meets (TLS, arrays, ranges, records, JSON as text, network and geometric types, full-text search; EnableUnmappedTypes is left out, being reflection — an enum or a domain the handlers do not know reads as <see cref="PostgresText.Unreadable"/>).</summary>
    public static NpgsqlDataSource Build(string connectionString) =>
        new NpgsqlSlimDataSourceBuilder(connectionString)
            .EnableTransportSecurity()
            .EnableArrays()
            .EnableRanges()
            .EnableMultiranges()
            .EnableRecords()
            .EnableJsonTypes()
            .EnableNetworkTypes()
            .EnableGeometricTypes()
            .EnableFullTextSearch()
            .EnableLTree()
            .EnableExtraConversions()
            .Build();

    /// <summary>
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (or the only one offered when
    /// blank), in the connection's own database: each one's rows read to <paramref name="maxRows"/>,
    /// each bound only the <c>@names</c> it uses, all inside one read-only transaction that is rolled back. A timeout, a failed connect
    /// and a server error are outcomes; a cancelled token throws.
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

        NoteUncheckedTls(target.Name, target.Config.TlsChecked);
        var secret = PostgresSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(secret.Value, timeoutSeconds);
        await using var source = Build(builder.ConnectionString);
        NpgsqlConnection connection;
        try
        {
            NpgsqlConnection? opened = null;
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("postgres", target.Config.Endpoint, target.Config.User?.Trim(), secret.Value, WrongPassword), async token => opened = await source.OpenConnectionAsync(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.Endpoint, PostgresConfigFile.Family.Tab), target.Name);
            }

            connection = opened!;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            string detail = PostgresText.WithCleartextHint(ex is PostgresException pg ? PostgresText.ServerError(pg.SqlState, pg.MessageText) : LogText.Excerpt(ex.Message.ReplaceLineEndings(" ")));
            DiagnosticLog.Warn(PostgresConfigFile.Category, PostgresText.ConnectFailedLogLine(target.Name, detail));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, detail, target.Name);
        }

        await using var held = connection;
        string workIn = connection.Database ?? builder.Database ?? "";
        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();

        // The session's settings read back before the model's text runs (2026-10-24, the database review's H1, mechanism 7): they ride the
        // startup options, which a pooler such as PgBouncer can drop without a word — and then the read-only default, the string rule the
        // gate reads by, the timeouts and the schema lock were all gone.
        if (await SessionRefusalAsync(connection, target.Config, readOnly: true, cancellationToken).ConfigureAwait(false) is { } notSet)
        {
            return SqlRun.Refused(SqlOutcome.Failed, notSet, target.Name);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                {
                    await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                for (int i = 0; i < statements.Count; i++)
                {
                    // One deadline for the whole call (2026-10-24, the database review's O13: each statement had the full timeout).
                    if (SqlDeadline.Left(watch, timeoutSeconds) is not (> 0 and var left))
                    {
                        return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
                    }

                    grids.Add(await ReadAsync(connection, transaction, statements[i], parameters, maxRows, left, last: i == statements.Count - 1, cancellationToken, export).ConfigureAwait(false));
                }
            }
            finally
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
                {
                    // The connection broke, or the transaction already ended with the error: nothing to undo.
                }
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is NpgsqlException or InvalidOperationException)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == QueryCanceled)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (PostgresException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex) + OperatorBindHint(ex.SqlState, ex.MessageText, statements, parameters.Select(p => p.Name)), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (NpgsqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, LogText.Excerpt(ex.Message.ReplaceLineEndings(" "), 600), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed);
    }

    /// <summary>
    /// <c>postgres_execute</c>'s run (2026-10-05, <c>PostgreSQL mode</c> <c>read-write</c>): one statement the write gate passed, on a
    /// connection whose entry says <c>"access": "readwrite"</c> (any other is <see cref="SqlOutcome.ReadOnlyConnection"/>, whatever the
    /// caller checked), in the connection's own database. The session as for a read but for <c>default_transaction_read_only</c>
    /// (<see cref="PostgresConnectionConfig.Builder"/>: <c>statement_timeout</c>, <c>lock_timeout</c> and the strings stay), and no
    /// transaction of the app's: the statement commits on its own, atomically, so VACUUM runs and a failed one changes nothing. The rows a
    /// RETURNING gives back are read to <paramref name="maxRows"/> (the rest drained, never cancelled: a cancel would undo the
    /// statement); <see cref="SqlRun.Changes"/> is the rows the server counts (null for DDL). A timeout, a failed connect and a server
    /// error are outcomes; a cancelled token throws (the server undoes the statement).
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
        var secret = PostgresSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(secret.Value, timeoutSeconds, readOnly: false);
        await using var source = Build(builder.ConnectionString);
        NpgsqlConnection connection;
        try
        {
            NpgsqlConnection? opened = null;
            if (await Sql.DatabaseSignIn.OpenAsync(Sql.DatabaseSignIn.Call("postgres", target.Config.Endpoint, target.Config.User?.Trim(), secret.Value, WrongPassword), async token => opened = await source.OpenConnectionAsync(token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false))
            {
                return SqlRun.Refused(SqlOutcome.ConnectFailed, Sql.SqlText.SignInHeld(target.Config.User?.Trim() ?? "", target.Config.Endpoint, PostgresConfigFile.Family.Tab), target.Name);
            }

            connection = opened!;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            string detail = PostgresText.WithCleartextHint(ex is PostgresException pg ? PostgresText.ServerError(pg.SqlState, pg.MessageText) : LogText.Excerpt(ex.Message.ReplaceLineEndings(" ")));
            DiagnosticLog.Warn(PostgresConfigFile.Category, PostgresText.ConnectFailedLogLine(target.Name, detail));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, detail, target.Name);
        }

        await using var held = connection;
        string workIn = connection.Database ?? builder.Database ?? "";
        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();

        // The session's settings read back before the model's text runs (2026-10-24, the database review's H1, mechanism 7): they ride the
        // startup options, which a pooler such as PgBouncer can drop without a word — and then the read-only default, the string rule the
        // gate reads by, the timeouts and the schema lock were all gone.
        if (await SessionRefusalAsync(connection, target.Config, readOnly: false, cancellationToken).ConfigureAwait(false) is { } notSet)
        {
            return SqlRun.Refused(SqlOutcome.Failed, notSet, target.Name);
        }

        int? changes;
        try
        {
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = timeoutSeconds + 5 };
            foreach (string bind in PostgresReadOnlyGate.Binds(sql, parameters.Select(p => p.Name)))
            {
                var value = parameters.FirstOrDefault(p => string.Equals(p.Name, bind, StringComparison.OrdinalIgnoreCase));
                command.Parameters.Add(Bind(bind, value?.Value));
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
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is NpgsqlException or InvalidOperationException)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == QueryCanceled)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException)
        {
            return new SqlRun(SqlOutcome.Timeout, timeoutSeconds.ToString(CultureInfo.InvariantCulture), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (PostgresException ex)
        {
            return new SqlRun(SqlOutcome.Failed, Message(ex) + OperatorBindHint(ex.SqlState, ex.MessageText, [sql], parameters.Select(p => p.Name)), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (NpgsqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, LogText.Excerpt(ex.Message.ReplaceLineEndings(" "), 600), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed) { Changes = changes };
    }

    /// <summary>One statement's rows: the <c>@names</c> it uses bound, read to the cap; the last statement cancelled on the server when more rows are left (its cancel answer then expected), so a huge result is not drained.</summary>
    private static async Task<SqlGrid> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, bool last, CancellationToken cancellationToken, SqlExportJob? export = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = timeoutSeconds + 5 };
        var named = PostgresReadOnlyGate.Binds(sql, parameters.Select(p => p.Name));
        foreach (string bind in named)
        {
            var value = parameters.FirstOrDefault(p => string.Equals(p.Name, bind, StringComparison.OrdinalIgnoreCase));
            command.Parameters.Add(Bind(bind, value?.Value));
        }

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        SqlGrid? grid = null;
        try
        {
            grid = export is null
                ? await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false)
                : await export.ReadAsync(reader, maxRows, cancellationToken).ConfigureAwait(false);
            if (grid.More && last)
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
            catch (PostgresException ex) when (grid is { More: true } && last && ex.SqlState == QueryCanceled)
            {
                // The cancel the cap sent, answered on the way out: the rows read are the result.
            }
        }

        return grid!;
    }

    /// <summary>A placeholder bound by name: text, whole numbers, decimals, floats and booleans as themselves; a null as text, so a <c>@x::text IS NULL</c> binds.</summary>
    public static NpgsqlParameter Bind(string name, object? value) => value switch
    {
        null => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value },
        string s => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = s },
        long l => new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = l },
        int i => new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = i },
        decimal d => new NpgsqlParameter(name, NpgsqlDbType.Numeric) { Value = d },
        double f => new NpgsqlParameter(name, NpgsqlDbType.Double) { Value = f },
        bool b => new NpgsqlParameter(name, NpgsqlDbType.Boolean) { Value = b },
        var other => new NpgsqlParameter(name, other),
    };

    /// <summary>
    /// A query's rows written to a workbook (2026-10-29, the Office writing plan): <see cref="RunAsync"/>'s read-only transaction and
    /// rollback, the rows handed to <paramref name="export"/> (typed by <see cref="ExportCell"/>, headed by <see cref="ExportHeader"/>)
    /// instead of read into a grid.
    /// </summary>
    public Task<SqlRun> ExportAsync(string? name, string statement, IReadOnlyList<SqlParameterValue> parameters, SqlExportJob export, int maxRows, int timeoutSeconds, CancellationToken cancellationToken) =>
        RunAsync(name, [statement], parameters, maxRows, timeoutSeconds, cancellationToken, export ?? throw new ArgumentNullException(nameof(export)));

    /// <summary>
    /// A PostgreSQL cell for a workbook (2026-10-29): an interval as PostgreSQL writes it (<see cref="IntervalText"/>: a month has no fixed
    /// length), an array as <c>{a, b}</c>, a numeric past <see cref="decimal"/> its exact digits (<see cref="NumericText"/>; text past 15) and
    /// NaN or an infinity as the word; money a number, as Npgsql hands it (2026-10-30, review F16/F117, the live run: money came as a bare
    /// decimal with no currency, so the text it was written as carried nothing more, while a month's interval, NaN and a numeric past
    /// <see cref="decimal"/> all wrote the can't-be-read placeholder); dates, timestamps and
    /// the rest by the common rules. A timestamptz is its UTC value, the column's header saying so (<see cref="ExportHeader"/>). A
    /// <c>±infinity</c> date or timestamp (Npgsql's <see cref="DateTime.MaxValue"/>/<see cref="DateTime.MinValue"/>, exactly) is the word,
    /// never a date in 9999 (2026-10-29, review P1-1: a real 9999-12-31 12:00 read as infinity too; a real 0001-01-01 00:00 still can't be
    /// told from <c>-infinity</c>). A <c>date</c> column is the exception: Npgsql hands its <c>infinity</c> as 9999-12-31 at midnight (the live
    /// run of 2026-10-29), so there that day is the word, and a real 9999-12-31 date reads infinity too, the lesser harm than a
    /// date in 9999 for every open-ended row. A timetz is its time and offset as text (review P6-3: it came on a dummy date in year 1). Arrays, bit
    /// strings and hstore are written in full to the cell's cap (<see cref="ExportText"/>).
    /// </summary>
    public static XlsxValue ExportCell(DbDataReader reader, int ordinal, string? typeName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(ordinal))
        {
            return XlsxValue.Blank;
        }

        if (typeName == IntervalType)
        {
            return XlsxValue.OfText(ExportInterval(reader, ordinal));
        }

        if (typeName == NumericType)
        {
            try
            {
                return SqlExport.FromValue(reader.GetValue(ordinal), typeName, ExportText);
            }
            catch (Exception ex) when (ex is InvalidCastException or OverflowException)
            {
                return ExportNumeric(reader, ordinal);
            }
        }

        if (typeName == TimeTzType && reader.GetValue(ordinal) is DateTimeOffset timetz)
        {
            return XlsxValue.OfText(TimeTz(timetz));
        }

        var cell = SqlExport.Typed(reader, ordinal, typeName, ExportText);
        if (cell.Kind is XlsxKind.Date or XlsxKind.DateTime)
        {
            if (cell.When == DateTime.MaxValue || (typeName == DateType && cell.When == DateTime.MaxValue.Date))
            {
                return XlsxValue.OfText(Infinity);
            }

            if (cell.When == DateTime.MinValue)
            {
                return XlsxValue.OfText("-" + Infinity);
            }
        }

        return cell;
    }

    /// <summary>The type name Npgsql gives an <c>interval</c> column.</summary>
    private const string IntervalType = "interval";

    /// <summary>The type name Npgsql gives a <c>numeric</c> column.</summary>
    private const string NumericType = "numeric";

    /// <summary>An interval cell for a workbook (<see cref="ReadInterval"/>), the can't-be-read placeholder when it won't read.</summary>
    private static string ExportInterval(DbDataReader reader, int ordinal) => ReadInterval(reader, ordinal) ?? SqlExportText.Unreadable(IntervalType);

    /// <summary>
    /// An interval cell as PostgreSQL writes it: read whole (a month part won't read as a <see cref="TimeSpan"/>), else as the
    /// <see cref="TimeSpan"/> the reader hands over; null when neither reads. The chat's and the workbook's one reading (2026-10-30, the
    /// user's call after review F16: a month's interval was "can't be read" in the chat, a day's <c>1.02:00:00</c>).
    /// </summary>
    private static string? ReadInterval(DbDataReader reader, int ordinal)
    {
        try
        {
            return IntervalText(reader.GetFieldValue<NpgsqlInterval>(ordinal));
        }
        catch (InvalidCastException)
        {
            // Not Npgsql's reader (a test's): the TimeSpan below.
        }

        try
        {
            return reader.GetValue(ordinal) is TimeSpan span
                ? IntervalText(new NpgsqlInterval(0, span.Days, (span.Ticks - span.Days * TimeSpan.TicksPerDay) / (TimeSpan.TicksPerMillisecond / 1000)))
                : null;
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// An interval as PostgreSQL writes it (its default <c>IntervalStyle</c>, <c>postgres</c>): <c>1 year 2 mons 3 days 04:05:06.5</c>,
    /// a field after a negative one signed (<c>-1 days +01:00:00</c>), <c>00:00:00</c> for none. Pure.
    /// </summary>
    public static string IntervalText(NpgsqlInterval value)
    {
        var sb = new StringBuilder();
        bool before = false;
        void Part(long amount, string unit)
        {
            if (amount == 0)
            {
                return;
            }

            sb.Append(sb.Length > 0 ? " " : "").Append(before && amount > 0 ? "+" : "").Append(amount.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(unit).Append(amount != 1 ? "s" : "");
            before = amount < 0;
        }

        Part(value.Months / 12, "year");
        Part(value.Months % 12, "mon");
        Part(value.Days, "day");
        long time = value.Time;
        if (time != 0 || sb.Length == 0)
        {
            ulong whole = time < 0 ? (ulong)-time : (ulong)time;
            ulong micro = whole % 1_000_000, seconds = whole / 1_000_000;
            sb.Append(sb.Length > 0 ? " " : "").Append(time < 0 ? "-" : before ? "+" : "")
                .Append((seconds / 3600).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append((seconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture)).Append(':')
                .Append((seconds % 60).ToString("00", CultureInfo.InvariantCulture));
            if (micro != 0)
            {
                sb.Append('.').Append(micro.ToString("000000", CultureInfo.InvariantCulture).TrimEnd('0'));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A numeric cell <see cref="DbDataReader.GetValue"/> won't hand over (NaN, an infinity, past <see cref="decimal"/>), read from its
    /// wire form (<see cref="NumericText"/>): the word as text, a number by the 15-digit rule; else the provider value's text
    /// (<see cref="SqlExport.FromProvider"/>, which writes the can't-be-read placeholder when that fails too).
    /// </summary>
    private static XlsxValue ExportNumeric(DbDataReader reader, int ordinal)
    {
        try
        {
            long length = reader.GetBytes(ordinal, 0, null, 0, 0);
            if (length is > 0 and <= MaxNumericBytes)
            {
                var wire = new byte[length];
                if (reader.GetBytes(ordinal, 0, wire, 0, wire.Length) == length && NumericText(wire) is { } text)
                {
                    return text is NumericNaN or NumericInfinity or "-" + NumericInfinity ? XlsxValue.OfText(text) : XlsxValue.OfNumberText(text);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
        {
            // A reader that holds no wire form (a test's): the provider value below.
        }

        return SqlExport.FromProvider(reader, ordinal, NumericType);
    }

    /// <summary>The longest numeric wire form read: past it a cell's digits are beyond its cap anyway.</summary>
    private const int MaxNumericBytes = 64 * 1024;

    private const string NumericNaN = "NaN";
    private const string NumericInfinity = "Infinity";

    /// <summary>
    /// A numeric's text from its binary wire form (four 16-bit fields — digit count, weight, sign, display scale — then base-10000
    /// digits), as PostgreSQL writes it: <c>NaN</c>, <c>Infinity</c>, <c>-Infinity</c>, or every digit to its scale. Null when the bytes
    /// aren't one. Pure.
    /// </summary>
    public static string? NumericText(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 8)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadInt16BigEndian(wire);
        int weight = BinaryPrimitives.ReadInt16BigEndian(wire[2..]);
        ushort sign = BinaryPrimitives.ReadUInt16BigEndian(wire[4..]);
        int scale = BinaryPrimitives.ReadUInt16BigEndian(wire[6..]) & 0x3FFF;
        switch (sign)
        {
            case 0xC000:
                return NumericNaN;
            case 0xD000:
                return NumericInfinity;
            case 0xF000:
                return "-" + NumericInfinity;
            case 0x0000 or 0x4000:
                break;
            default:
                return null;
        }

        if (count < 0 || wire.Length != 8 + 2 * count)
        {
            return null;
        }

        var digits = new int[count];
        for (int i = 0; i < count; i++)
        {
            digits[i] = BinaryPrimitives.ReadInt16BigEndian(wire[(8 + 2 * i)..]);
            if (digits[i] is < 0 or > 9999)
            {
                return null;
            }
        }

        int Group(int index) => index >= 0 && index < count ? digits[index] : 0;
        var sb = new StringBuilder();
        if (sign == 0x4000)
        {
            sb.Append('-');
        }

        if (weight < 0)
        {
            sb.Append('0');
        }
        else
        {
            sb.Append(Group(0).ToString(CultureInfo.InvariantCulture));
            for (int g = 1; g <= weight; g++)
            {
                sb.Append(Group(g).ToString("0000", CultureInfo.InvariantCulture));
            }
        }

        if (scale > 0)
        {
            sb.Append('.');
            int start = sb.Length;
            for (int g = weight + 1; sb.Length - start < scale; g++)
            {
                sb.Append(Group(g).ToString("0000", CultureInfo.InvariantCulture));
            }

            sb.Length = start + scale;
        }

        return sb.ToString();
    }

    /// <summary>PostgreSQL's word for a date past every other.</summary>
    private const string Infinity = "infinity";

    /// <summary>The type name Npgsql gives a <c>date</c> column.</summary>
    private const string DateType = "date";

    /// <summary>The type name Npgsql gives a <c>timetz</c> column.</summary>
    private const string TimeTzType = "time with time zone";

    /// <summary>A timetz as PostgreSQL writes one: the time and its offset (Npgsql hands it on a dummy date, which is no part of it).</summary>
    public static string TimeTz(DateTimeOffset value) => value.ToString("HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// A value the sheet can't type, as PostgreSQL would write it, in full to the cell's cap (2026-10-29, review P5-1: the chat's text cut
    /// it at 500 characters): an array as <c>{a, b}</c>, a bit string as its 0s and 1s, an hstore as <c>"k"=>"v"</c>; anything else its
    /// invariant text.
    /// </summary>
    public static string ExportText(object value) => value switch
    {
        Array array and not byte[] => ExportArrayText(array),
        BitArray bits => Bits(bits),
        IEnumerable<KeyValuePair<string, string?>> hstore => Hstore(hstore),
        _ => SqlExport.InvariantText(value),
    };

    /// <summary>An array as <see cref="ArrayText"/> writes it, unclipped and unscrubbed, built no further than the cell's cap.</summary>
    public static string ExportArrayText(Array array)
    {
        ArgumentNullException.ThrowIfNull(array);
        var sb = new StringBuilder();
        AppendDimension(sb, array, 0, new int[array.Rank], ExportElement, XlsxValue.MaxChars);
        return sb.ToString();
    }

    private static string ExportElement(object? item) => item switch
    {
        null or DBNull => SqlText.Null,
        DateTime { Kind: DateTimeKind.Utc } utc => SqlExport.InvariantText(utc) + " UTC",
        _ => SqlExport.InvariantText(item),
    };

    /// <summary>A bit string as PostgreSQL writes one: its bits in order, 0 and 1.</summary>
    public static string Bits(BitArray bits)
    {
        ArgumentNullException.ThrowIfNull(bits);
        var chars = new char[bits.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = bits[i] ? '1' : '0';
        }

        return new string(chars);
    }

    /// <summary>An hstore as PostgreSQL writes one: <c>"k"=>"v", "n"=>NULL</c>, quotes and backslashes escaped.</summary>
    public static string Hstore(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return string.Join(", ", pairs.Select(p => Quoted(p.Key) + "=>" + (p.Value is null ? SqlText.Null : Quoted(p.Value))));
    }

    /// <summary>A column's header in a workbook: its name, and <see cref="PostgresText.UtcColumn"/> on a timestamptz, whose cells hold UTC.</summary>
    public static string ExportHeader(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);
        string name = reader.GetName(ordinal);
        if (string.IsNullOrEmpty(name))
        {
            name = SqlText.UnnamedColumn(ordinal);
        }

        return reader.GetDataTypeName(ordinal) == "timestamp with time zone" ? name + PostgresText.UtcColumn : name;
    }

    /// <summary>One cell as text: <see cref="SqlText.Cell"/>, an array as <c>{a, b}</c>; a value the client cannot hold (a numeric past <see cref="decimal"/>) as <see cref="PostgresText.Unreadable"/>.</summary>
    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return SqlText.Null;
            }

            // An interval as PostgreSQL writes it, a month's too (2026-10-30, the user's call after review F16), as the workbook has it.
            if (reader.GetDataTypeName(ordinal) == IntervalType)
            {
                return ReadInterval(reader, ordinal) is { } interval ? SqlText.Cell(interval) : PostgresText.Unreadable(IntervalType);
            }

            object value = reader.GetValue(ordinal);
            // A timestamptz comes back as UTC (Npgsql's DateTime of Kind Utc): said so, since the cell alone would not (2026-10-04).
            // A timetz is its time and offset, not the dummy date Npgsql puts it on (2026-10-29, review P6-3).
            return value switch
            {
                Array array and not byte[] => ArrayText(array),
                DateTime { Kind: DateTimeKind.Utc } utc => SqlText.Cell(utc) + " UTC",
                DateTimeOffset timetz when reader.GetDataTypeName(ordinal) == TimeTzType => TimeTz(timetz),
                _ => SqlText.Cell(value),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException or FormatException)
        {
            return PostgresText.Unreadable(reader.GetDataTypeName(ordinal));
        }
    }

    /// <summary>
    /// An array as PostgreSQL writes one: each dimension in its own braces (2026-10-24, the database review's O10: a two-dimensional array
    /// was flattened), a timestamptz element said UTC as a cell is, and the whole clipped to <see cref="SqlText.MaxCellChars"/> — built no
    /// further than twice that (B11: an array_agg of a big table gave a cell of megabytes, and no row fit the text cap).
    /// </summary>
    public static string ArrayText(Array array)
    {
        ArgumentNullException.ThrowIfNull(array);
        var sb = new StringBuilder();
        bool cut = !AppendDimension(sb, array, 0, new int[array.Rank], ChatElement, SqlText.MaxCellChars * 2);
        string text = sb.ToString();
        return cut ? SqlText.Cell(Files.TextCut.Safe(text, SqlText.MaxCellChars)) + SqlText.MoreChars(SqlText.MaxCellChars) : SqlText.Cell(text);
    }

    private static string ChatElement(object? item) => item switch
    {
        null or DBNull => SqlText.Null,
        DateTime { Kind: DateTimeKind.Utc } utc => SqlText.Cell(utc) + " UTC",
        _ => SqlText.Cell(item),
    };

    /// <summary>
    /// One dimension's elements into <paramref name="sb"/>, each written by <paramref name="element"/>; false when the text grew past
    /// <paramref name="limit"/> and was stopped.
    /// </summary>
    private static bool AppendDimension(StringBuilder sb, Array array, int dimension, int[] index, Func<object?, string> element, int limit)
    {
        sb.Append('{');
        for (int k = array.GetLowerBound(dimension); k <= array.GetUpperBound(dimension); k++)
        {
            if (sb.Length > limit)
            {
                return false;
            }

            if (k > array.GetLowerBound(dimension))
            {
                sb.Append(", ");
            }

            index[dimension] = k;
            if (dimension + 1 < array.Rank)
            {
                if (!AppendDimension(sb, array, dimension + 1, index, element, limit))
                {
                    return false;
                }
            }
            else
            {
                sb.Append(element(array.GetValue(index)));
            }
        }

        sb.Append('}');
        return true;
    }

    /// <summary>The server's message with its SQLSTATE — <c>42P01: relation "x" does not exist</c> — and a sentence for the read-only refusal.</summary>
    public static string Message(PostgresException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string text = PostgresText.ServerError(ex.SqlState, LogText.Excerpt(ex.MessageText.ReplaceLineEndings(" "), 600));
        return ex.SqlState == ReadOnlyTransaction ? PostgresText.ReadOnlyRefused + " (" + text + ")" : text;
    }

    /// <summary>
    /// What follows the server's message when the error is an operator or a syntax one about the operator an <c>@name</c> straight
    /// after an operator makes when params does not name it (<see cref="PostgresReadOnlyGate.UnboundOperators"/>): the likely cause,
    /// said; else empty. The message must name that operator — <c>operator does not exist: integer =@ integer</c>, <c>syntax error at
    /// or near "=@"</c> — so a misspelt function's error beside a correct <c>v @@to_tsquery(…)</c> gets no hint to bind to_tsquery
    /// (the fourth 2026-10-04 review).
    /// </summary>
    public static string OperatorBindHint(string sqlState, string? message, IEnumerable<string> statements, IEnumerable<string> named)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (sqlState is not (UndefinedFunction or SyntaxError) || string.IsNullOrEmpty(message))
        {
            return "";
        }

        var names = named.ToList();
        var unbound = statements.SelectMany(s => PostgresReadOnlyGate.UnboundOperators(s, names))
            .Where(u => NamesOperator(message, u.Operator))
            .Select(u => u.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return unbound.Count == 0 ? "" : PostgresText.UnboundOperatorBind(unbound);
    }

    /// <summary>Whether the server's message names <paramref name="op"/> as a whole operator: between blanks, after <c>: </c>, or quoted.</summary>
    private static bool NamesOperator(string message, string op) =>
        message.Contains("\"" + op + "\"", StringComparison.Ordinal)
        || Regex.IsMatch(message, @"(?:^|\s)" + Regex.Escape(op) + @"(?:\s|$)", RegexOptions.CultureInvariant);

    /// <summary>A table reference split into schema and name: <c>public.orders</c>, <c>orders</c>, <c>"My.Table"</c> (a doubled quote is one). Null when it is not one or two names.</summary>
    public static (string? Schema, string Name)? Split(string? reference)
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
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
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
