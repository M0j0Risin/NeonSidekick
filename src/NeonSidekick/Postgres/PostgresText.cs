using System.Globalization;
using System.Text;
using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// The PostgreSQL tools' wording and formatting (2026-10-04), <c>MySqlText</c>'s part for PostgreSQL, pure and pinned: the
/// <c>postgres.json</c> problems, the gate's refusals, the outcomes and the results. The engine-neutral pieces are
/// <see cref="SqlText"/>'s. Every error starts <c>Error:</c>; a place is written <c>connection/database</c>. Invariant culture.
/// </summary>
public static class PostgresText
{
    // ─── postgres.json ──────────────────────────────────────────────────────────

    public const string NoHost = "no \"host\" is given";
    public const string NoUser = "no \"user\" is given";
    public static string BadPort(int port) => $"\"port\" is {Invariant(port)}; it must be 1 to 65535";
    public static string BadSslMode(string word) => $"\"sslMode\" is '{word}'; it must be prefer, require, verify-ca, verify-full or disable";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on /tools › Database › Postgres (PostgreSQL set password)";
    public static string NoCredential(string target) => $"no password in {SqlText.CredentialStore} for {target}; set it on /tools › Database › Postgres, or: {SqlText.CredentialCommand(target, "<user>")}";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's postgres.json was checked for plain passwords: {detail}";
    public const string NoPasswordConnections = "No connection in postgres.json yet; add one first (PostgreSQL add connection).";

    /// <summary>One connection as <c>postgres_connections</c> lists it; a <c>readwrite</c> one says so (2026-10-05), and whether <paramref name="writes"/> (<c>PostgreSQL mode</c> read-write) lets it change.</summary>
    public static string ConnectionLine(PostgresNamedConnection connection, bool writes = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string database = $" / {config.Database?.Trim()}" + (string.IsNullOrWhiteSpace(config.Schema) ? "" : $" (schema {config.Schema.Trim()} alone)");
        string line = $"- {connection.Name}: {config.Endpoint}{database}, user {config.User?.Trim()}{ServerWriteText.AccessNote(PostgresStatementKinds.Family, config.IsReadWrite, writes)}";
        return string.IsNullOrWhiteSpace(config.Description) ? line : line + " — " + config.Description.Trim();
    }

    public static string HiddenConnections(int count) =>
        count == 1
            ? "1 more connection in postgres.json is switched off for this profile (/tools › Database › Postgres)."
            : $"{Invariant(count)} more connections in postgres.json are switched off for this profile (/tools › Database › Postgres).";

    public static string MentionNote(PostgresNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string where = "PostgreSQL · " + config.Endpoint + " / " + config.Database?.Trim() + (string.IsNullOrWhiteSpace(config.Schema) ? "" : "." + config.Schema.Trim());
        return string.IsNullOrWhiteSpace(config.Description) ? where : where + " — " + config.Description.Trim();
    }

    public static string Connections(PostgresCatalog catalog, bool writes = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Connections.Count == 0)
        {
            sb.Append(NoConnections);
        }
        else
        {
            sb.Append(SqlText.Count(catalog.Connections.Count, "PostgreSQL connection")).Append(" (every PostgreSQL tool takes one by name in \"connection\"; it may be left out only when there is one):");
            foreach (var connection in catalog.Connections)
            {
                sb.Append('\n').Append(ConnectionLine(connection, writes));
            }
        }

        foreach (var problem in catalog.Problems)
        {
            sb.Append("\nSkipped ").Append(problem.Source).Append(": ").Append(problem.Reason);
        }

        if (catalog.Hidden > 0)
        {
            sb.Append('\n').Append(HiddenConnections(catalog.Hidden));
        }

        return sb.ToString();
    }

    // ─── the gate ───────────────────────────────────────────────────────────────

    public const string NoSql = "Error: give the SELECT to run in \"sql\"";
    public const string SelectInto = "Error: SELECT … INTO makes a table; the PostgreSQL tools only read — drop the INTO";
    public const string Positional = "Error: the SQL uses a $1 placeholder; name it (@id) and pass its value in \"params\"";
    public static string Unterminated(string what, int line, int column) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): a {what} never ends";
    public static string UnicodeEscapes(int line, int column) => $"Error: the SQL uses a U& string or name (line {Invariant(line)}, column {Invariant(column)}); write the text plainly";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one SELECT per call (a WITH clause may lead it)";
    public static string NotASelect(string word) => $"Error: the SQL starts with {word}; the PostgreSQL tools only read — send one SELECT (a WITH clause may lead it)";
    public static string Forbidden(string what) => $"Error: the SQL uses {what}, which the PostgreSQL tools refuse (it changes something a rollback cannot undo, or reaches outside the query)";

    // ─── the connection's database and schema (2026-10-09, PostgresBoundary) ────

    /// <summary>An entry with no <c>database</c>: a connection reaches that one database alone, so it must name it. Pinned.</summary>
    public const string NoDatabase = "no \"database\" is given; a connection reaches one database, so it must name it";

    /// <summary>A <c>schema</c> key the session's options cannot carry. Pinned.</summary>
    public static string BadSchemaKey(string schema) => $"\"schema\" is '{schema}'; a schema to hold the connection to is one name, without spaces, commas, quotes or backslashes";

    /// <summary>A call that still sends <c>database</c>. Pinned.</summary>
    public const string NoDatabaseArgument =
        "Error: \"database\" is not an argument: a PostgreSQL connection reaches its own database alone (postgres_connections names each one's); another database needs a connection of its own, which the user adds";

    /// <summary>A view or function of the whole cluster rather than the database. Pinned.</summary>
    public static string ServerCatalog(string named) =>
        $"Error: the SQL reads {named}, which describes the whole server rather than this connection's database; the PostgreSQL tools keep to the connection's database (postgres_tables, postgres_columns and postgres_describe read its catalog)";

    /// <summary>A qualifier naming another schema under a schema lock (or an alias spelled like one). Pinned.</summary>
    public static string OtherSchema(string named, string schema) =>
        $"Error: the SQL names '{named}', another schema of this database (an alias spelled like it reads the same: rename it), but this connection is held to schema '{schema}'; another schema needs a connection of its own, which the user adds";

    /// <summary>A function that reads a table, schema or database named in a string, under a schema lock (the review, 2026-10-09). Pinned.</summary>
    public static string XmlExport(string named, string schema) =>
        $"Error: the SQL uses {named}, which reads the table, schema or database its argument names, but this connection is held to schema '{schema}'; select from the schema's tables instead";

    /// <summary>A tool's <c>schema</c> (or a <c>schema.name</c> table) naming another under a schema lock. Pinned.</summary>
    public static string SchemaElsewhere(string named, string schema) =>
        $"Error: this connection is held to schema '{schema}', so '{named}' is out of its reach; leave \"schema\" out or give {schema}";

    /// <summary>
    /// What else the role could reach (the wizard's test and <c>--postgres-check</c>, 2026-10-09): the other databases it may connect to
    /// (PUBLIC may connect to a new database unless that is revoked), the foreign servers it may use, and under a schema lock the other
    /// schemas it may use; or that it reaches none. Pinned.
    /// </summary>
    public static string Reach(string database, string? schema, IReadOnlyList<string> databases, IReadOnlyList<string> servers, IReadOnlyList<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(schemas);
        static string Names(IReadOnlyList<string> names) => string.Join(", ", names.Take(10)) + (names.Count > 10 ? $" and {Invariant(names.Count - 10)} more" : "");
        string place = string.IsNullOrWhiteSpace(schema) ? database : database + "." + schema.Trim();
        var parts = new List<string>();
        if (databases.Count > 0)
        {
            parts.Add("connect to " + Names(databases));
        }

        if (servers.Count > 0)
        {
            parts.Add("use the foreign servers " + Names(servers));
        }

        if (!string.IsNullOrWhiteSpace(schema) && schemas.Count > 0)
        {
            parts.Add("use the schemas " + Names(schemas));
        }

        // An empty report says nothing more is in reach than the lists cover (the review, 2026-10-09): the system catalogs are every role's.
        return parts.Count == 0
            ? $"The role reaches no other database, foreign server{(string.IsNullOrWhiteSpace(schema) ? "" : " or schema")} beyond {place} (the system catalogs aside)."
            : $"The role could also {string.Join("; ", parts)}. The tools keep to {place}; a role granted only it is the lock the server keeps too.";
    }

    // ─── arguments ──────────────────────────────────────────────────────────────

    public const string NoTable = "Error: give the table or view in \"table\" (schema.name, e.g. public.orders)";
    public const string NoPattern = "Error: give the column name (or part of it) to look for in \"pattern\"";
    public static string BadTable(string table) => $"Error: '{table}' is not a table name (name or schema.name; \"double quotes\" around a name with dots or capitals)";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5}} for @id (got {Clip(raw, 200)})";
    public static string TableNotFound(string table, string where) => $"Error: no table or view '{table}' visible in {where}; postgres_tables lists them";
    public static string TableAmbiguous(string table, IEnumerable<string> candidates) => $"Error: '{table}' matches more than one table or view; give the schema: {string.Join(", ", candidates)}";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoConnections = "Error: no PostgreSQL connection is defined; the user adds one to postgres.json (/tools › Database › Postgres)";
    public static string UnknownConnection(string name, string names) => $"Error: no PostgreSQL connection is named '{name}'; the connections are {names}";

    /// <summary>A call that names no connection while several are offered (2026-10-09: the default-connection settings gone). Pinned.</summary>
    public static string ConnectionNeeded(string names) => $"Error: name the PostgreSQL connection in \"connection\"; the connections are {names}";
    public static string ConnectFailed(string connection, string detail) => $"Error: could not connect to {connection}: {detail}";
    public static string Timeout(string connection, string seconds) => $"Error: the query on {connection} ran past {seconds} s and was stopped; narrow it (WHERE, LIMIT, fewer joins)";
    public static string Failed(string connection, string detail) => $"Error: the server refused the SQL ({connection}): {detail}";
    public static string ServerError(string sqlState, string message) => $"{sqlState}: {message}";
    public const string ReadOnlyRefused = "the read-only transaction refused a change";

    /// <summary>The hint after an operator or syntax error when an <c>@name</c> straight after an operator was left unbound (<see cref="PostgresAccess.OperatorBindHint"/>).</summary>
    public static string UnboundOperatorBind(IReadOnlyList<string> names) =>
        $"; {string.Join(", ", names.Select(n => "@" + n))} straight after an operator is a placeholder only when params names it, else the server reads the @ as part of the operator: pass {string.Join(", ", names)} in params";
    public static string ConnectFailedLogLine(string connection, string detail) => $"{connection} did not connect: {detail}";

    public static string Error(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Outcome switch
        {
            SqlOutcome.NoConnections => NoConnections,
            SqlOutcome.UnknownConnection => run.Connection.Length == 0 ? ConnectionNeeded(run.Detail) : UnknownConnection(run.Connection, run.Detail),
            SqlOutcome.ConnectFailed => ConnectFailed(run.Connection, run.Detail),
            SqlOutcome.Timeout => Timeout(Where(run), run.Detail),
            SqlOutcome.ReadOnlyConnection => ServerWriteText.ReadOnlyConnection(PostgresStatementKinds.Family, run.Connection),
            _ => Failed(Where(run), run.Detail),
        };
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>A value the client cannot read: its type, and how to read it anyway.</summary>
    public static string Unreadable(string type) => $"({type}: select it as text — CAST(… AS text) or …::text)";

    /// <summary><c>connection/database</c>: where a run worked.</summary>
    public static string Where(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Database.Length == 0 ? run.Connection : run.Connection + "/" + run.Database;
    }

    public static string Query(SqlRun run, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Grids.Count == 0)
        {
            return $"The query returned no result set ({Where(run)}, {Millis(run.Elapsed)})";
        }

        var grid = run.Grids[0];
        string table = SqlText.Table(grid, maxChars, out int shown);
        var header = new StringBuilder();
        header.Append(SqlText.Count(grid.Rows.Count, "row")).Append(grid.More ? "+" : "")
            .Append(" × ").Append(SqlText.Count(grid.Columns.Count, "column"))
            .Append(" from ").Append(Where(run))
            .Append(" (").Append(Millis(run.Elapsed)).Append(')');
        if (grid.More)
        {
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown, more exist (narrow it with WHERE or LIMIT, or raise max_rows)");
        }

        if (shown < grid.Rows.Count)
        {
            header.Append(" — ").Append(Invariant(shown)).Append(" fit the text cap; select fewer columns or rows for the rest");
        }

        return header + "\n\n" + table;
    }

    /// <summary>A catalog listing: a header naming what and where, then the table (cut by the text cap, said so).</summary>
    public static string Listing(string singular, string plural, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = run.Grids.Count > 0 ? run.Grids[0] : new SqlGrid([], [], false);
        if (grid.Rows.Count == 0)
        {
            return $"No {plural} in {Where(run)}";
        }

        string table = SqlText.Table(grid, maxChars, out int shown);
        string header = $"{SqlText.Count(grid.Rows.Count, singular, plural)}{(grid.More ? "+" : "")} in {Where(run)}";
        if (grid.More || shown < grid.Rows.Count)
        {
            header += $" — the first {Invariant(shown)} shown; narrow it";
        }

        return header + "\n\n" + table;
    }

    /// <summary>
    /// <c>postgres_describe</c>'s answer, by position (<see cref="PostgresCatalogQueries.Describe"/>): the name, kind and comment, the
    /// columns (type as declared, nullability, default, the primary key's place, comment), the foreign keys out and in, the indexes
    /// and the CHECK constraints — each section only when it has a line.
    /// </summary>
    public static string Describe(string schema, string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var sb = new StringBuilder();
        var columns = Set(run, 0);
        sb.Append(schema).Append('.').Append(name).Append(" (").Append(kind).Append(", ").Append(SqlText.Count(columns.Rows.Count, "column")).Append(") in ").Append(Where(run)).Append('\n');
        if (Set(run, 5).Rows is [var described, ..] && At(described, 0) is { Length: > 0 } comment && comment != SqlText.Null)
        {
            sb.Append(comment.ReplaceLineEndings(" ")).Append('\n');
        }

        // column, type, nullable, default, pk, comment
        bool comments = columns.Rows.Any(c => At(c, 5) != SqlText.Null);
        sb.Append(comments ? "\n| column | type | null | key | default | comment |\n|---|---|---|---|---|---|\n" : "\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        foreach (var c in columns.Rows)
        {
            string key = At(c, 4) is SqlText.Null or "" or "0" ? "" : "PK " + At(c, 4);
            sb.Append("| ").Append(At(c, 0)).Append(" | ").Append(At(c, 1)).Append(" | ").Append(At(c, 2) == "true" ? "yes" : "no")
                .Append(" | ").Append(key).Append(" | ").Append(At(c, 3) == SqlText.Null ? "" : SqlText.TableCell(At(c, 3)));
            sb.Append(comments ? " | " + (At(c, 5) == SqlText.Null ? "" : SqlText.TableCell(At(c, 5))) + " |\n" : " |\n");
        }

        Section(sb, "Foreign keys", Set(run, 1), r => $"{At(r, 0)}: ({At(r, 1)}) → {At(r, 2)} ({At(r, 3)})");
        Section(sb, "Referenced by", Set(run, 2), r => $"{At(r, 0)} ({At(r, 1)}) → ({At(r, 2)})");
        Section(sb, "Indexes", Set(run, 3), r => At(r, 1));
        Section(sb, "Checks", Set(run, 4), r => $"{At(r, 0)}: {At(r, 1)}");
        string text = sb.ToString().TrimEnd();
        return text.Length <= maxChars ? text : text[..maxChars] + "\n… (cut at the text cap)";
    }

    private static void Section(StringBuilder sb, string title, SqlGrid grid, Func<string[], string> line)
    {
        if (grid.Rows.Count == 0)
        {
            return;
        }

        sb.Append('\n').Append(title).Append(":\n");
        foreach (var row in grid.Rows)
        {
            sb.Append("- ").Append(line(row)).Append('\n');
        }
    }

    /// <summary>The wizard's test line: who it signed in as, where, the server's version. Pinned.</summary>
    public static string TestOk(string name, string user, string database, string version) => $"Connected to '{name}' as {user} in {database}: {version}";

    /// <summary>
    /// The wizard's warning when the account could change data: what it may do. Pinned. A <c>readwrite</c> connection (2026-10-05)
    /// is told what may then change it: <c>postgres_execute</c>, each change allowed by the user.
    /// </summary>
    public static string CanWrite(IReadOnlyList<string> powers, bool readWrite = false) =>
        readWrite
            ? $"This account can change data ({string.Join(", ", powers)}). As readwrite, postgres_execute may use those powers under PostgreSQL mode read-write, each change allowed by you; the account is still the real guard."
            : $"This account can change data ({string.Join(", ", powers)}). The tools never will, but a SELECT-only role is the real guard.";

    private static SqlGrid Set(SqlRun run, int index) => index < run.Grids.Count ? run.Grids[index] : new SqlGrid([], [], false);

    private static string At(string[] row, int index) => index < row.Length ? row[index] : "";

    private static string Millis(TimeSpan elapsed) => Invariant((long)elapsed.TotalMilliseconds) + " ms";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
