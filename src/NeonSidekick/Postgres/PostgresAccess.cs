using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;
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
/// A call is a list of statements, one result set each, all in the one transaction. A database other than the connection's own is a
/// connection to that database (Postgres cannot switch inside a session). The password never leaves the connection string.
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

    public PostgresAccess(Func<PostgresCatalog> catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public PostgresCatalog Catalog() => _catalog();

    public PostgresNamedConnection? Resolve(string? name, string? defaultName, out SqlRun? refused)
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
    /// Runs <paramref name="statements"/> on the connection <paramref name="name"/> names (<paramref name="defaultName"/> or the first when
    /// blank), in <paramref name="database"/> when given (else the connection's own): each one's rows read to <paramref name="maxRows"/>,
    /// each bound only the <c>@names</c> it uses, all inside one read-only transaction that is rolled back. A timeout, a failed connect
    /// and a server error are outcomes; a cancelled token throws.
    /// </summary>
    public async Task<SqlRun> RunAsync(string? name, string? defaultName, string? database, IReadOnlyList<string> statements, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(parameters);
        if (Resolve(name, defaultName, out var refused) is not { } target)
        {
            return refused!;
        }

        var secret = PostgresSecrets.Resolve(target);
        if (secret.Error is { } missing)
        {
            return SqlRun.Refused(SqlOutcome.ConnectFailed, missing, target.Name);
        }

        var builder = target.Config.Builder(secret.Value, database, timeoutSeconds);
        await using var source = Build(builder.ConnectionString);
        NpgsqlConnection connection;
        try
        {
            connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException && !cancellationToken.IsCancellationRequested)
        {
            string detail = ex is PostgresException pg ? PostgresText.ServerError(pg.SqlState, pg.MessageText) : LogText.Excerpt(ex.Message.ReplaceLineEndings(" "));
            DiagnosticLog.Warn(PostgresConfigFile.Category, PostgresText.ConnectFailedLogLine(target.Name, detail));
            return SqlRun.Refused(SqlOutcome.ConnectFailed, detail, target.Name);
        }

        await using var held = connection;
        string workIn = connection.Database ?? builder.Database ?? "";
        var watch = Stopwatch.StartNew();
        var grids = new List<SqlGrid>();
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
                    grids.Add(await ReadAsync(connection, transaction, statements[i], parameters, maxRows, timeoutSeconds, last: i == statements.Count - 1, cancellationToken).ConfigureAwait(false));
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
            return new SqlRun(SqlOutcome.Failed, Message(ex) + OperatorBindHint(ex.SqlState, statements, parameters.Select(p => p.Name)), target.Name, workIn, grids, watch.Elapsed);
        }
        catch (NpgsqlException ex)
        {
            return new SqlRun(SqlOutcome.Failed, LogText.Excerpt(ex.Message.ReplaceLineEndings(" "), 600), target.Name, workIn, grids, watch.Elapsed);
        }

        return new SqlRun(SqlOutcome.Ok, "", target.Name, workIn, grids, watch.Elapsed);
    }

    /// <summary>One statement's rows: the <c>@names</c> it uses bound, read to the cap; the last statement cancelled on the server when more rows are left (its cancel answer then expected), so a huge result is not drained.</summary>
    private static async Task<SqlGrid> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, IReadOnlyList<SqlParameterValue> parameters, int maxRows, int timeoutSeconds, bool last, CancellationToken cancellationToken)
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
            grid = await SqlGrid.ReadAsync(reader, maxRows, cancellationToken, ReadCell).ConfigureAwait(false);
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

    /// <summary>One cell as text: <see cref="SqlText.Cell"/>, an array as <c>{a, b}</c>; a value the client cannot hold (a numeric past <see cref="decimal"/>) as <see cref="PostgresText.Unreadable"/>.</summary>
    private static string ReadCell(DbDataReader reader, int ordinal)
    {
        try
        {
            if (reader.IsDBNull(ordinal))
            {
                return SqlText.Null;
            }

            object value = reader.GetValue(ordinal);
            // A timestamptz comes back as UTC (Npgsql's DateTime of Kind Utc): said so, since the cell alone would not (2026-10-04).
            return value switch
            {
                Array array and not byte[] => ArrayText(array),
                DateTime { Kind: DateTimeKind.Utc } utc => SqlText.Cell(utc) + " UTC",
                _ => SqlText.Cell(value),
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or NotSupportedException or OverflowException or FormatException)
        {
            return PostgresText.Unreadable(reader.GetDataTypeName(ordinal));
        }
    }

    private static string ArrayText(Array array)
    {
        var sb = new StringBuilder("{");
        foreach (object? item in array)
        {
            sb.Append(sb.Length > 1 ? ", " : "").Append(item is null or DBNull ? SqlText.Null : SqlText.Cell(item));
        }

        return sb.Append('}').ToString();
    }

    /// <summary>The server's message with its SQLSTATE — <c>42P01: relation "x" does not exist</c> — and a sentence for the read-only refusal.</summary>
    public static string Message(PostgresException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string text = PostgresText.ServerError(ex.SqlState, LogText.Excerpt(ex.MessageText.ReplaceLineEndings(" "), 600));
        return ex.SqlState == ReadOnlyTransaction ? PostgresText.ReadOnlyRefused + " (" + text + ")" : text;
    }

    /// <summary>
    /// What follows the server's message when the error is an operator or a syntax one and a statement holds an <c>@name</c> straight
    /// after an operator that params does not name (<see cref="PostgresReadOnlyGate.UnboundOperatorBinds"/>): the likely cause,
    /// said; else empty.
    /// </summary>
    public static string OperatorBindHint(string sqlState, IEnumerable<string> statements, IEnumerable<string> named)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (sqlState is not (UndefinedFunction or SyntaxError))
        {
            return "";
        }

        var names = named.ToList();
        var unbound = statements.SelectMany(s => PostgresReadOnlyGate.UnboundOperatorBinds(s, names)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return unbound.Count == 0 ? "" : PostgresText.UnboundOperatorBind(unbound);
    }

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
