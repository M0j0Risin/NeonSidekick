using System.Globalization;
using System.Text;
using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// The MySQL tools' wording and formatting (2026-09-30), <see cref="Oracle.OracleText"/>'s part for MySQL and MariaDB, pure and
/// pinned: the <c>mysql.json</c> problems, the gate's refusals, the outcomes and the results. The engine-neutral pieces — a cell's
/// text, a Markdown table, a count — are <see cref="SqlText"/>'s own. Every error starts <c>Error:</c>; a place is written
/// <c>connection/database</c>. Invariant culture throughout.
/// </summary>
public static class MySqlText
{
    // ─── mysql.json ─────────────────────────────────────────────────────────────

    public const string NoHost = "no \"host\" is given";
    public const string NoUser = "no \"user\" is given";
    public static string BadPort(int port) => $"\"port\" is {Invariant(port)}; it must be 1 to 65535";
    public static string BadSslMode(string word) => $"\"sslMode\" is '{word}'; it must be preferred, required, verify-ca, verify-full or none";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on the MySQL tab of /tools (MySQL set password)";
    public static string NoCredential(string target) => $"no password in Windows Credential Manager for {target}; set it on the MySQL tab of /tools, or: cmdkey /generic:{target} /user:<user> /pass";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's mysql.json was checked for plain passwords: {detail}";
    public const string NoPasswordConnections = "No connection in mysql.json yet; add one first (MySQL add connection).";

    /// <summary>One connection as <c>mysql_connections</c> lists it: the name, where, the user, the database, the description — never the password.</summary>
    /// <remarks>A <c>readwrite</c> one says so (2026-10-05), and whether <paramref name="writes"/> (<c>MySQL mode</c> read-write) lets it change.</remarks>
    public static string ConnectionLine(MySqlNamedConnection connection, bool isDefault, bool writes = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string database = string.IsNullOrWhiteSpace(config.Database) ? "" : $" / {config.Database.Trim()}";
        string line = $"- {connection.Name}{(isDefault ? " (default)" : "")}: {config.Endpoint}{database}, user {config.User?.Trim()}{ServerWriteText.AccessNote(MySqlStatementKinds.Family, config.IsReadWrite, writes)}";
        return string.IsNullOrWhiteSpace(config.Description) ? line : line + " — " + config.Description.Trim();
    }

    /// <summary>The closing line of <c>mysql_connections</c> while the profile hides some: how many, never which. Pinned.</summary>
    public static string HiddenConnections(int count) =>
        count == 1
            ? "1 more connection in mysql.json is switched off for this profile (the MySQL tab of /tools)."
            : $"{Invariant(count)} more connections in mysql.json are switched off for this profile (the MySQL tab of /tools).";

    /// <summary>A connection's note on the <c>%</c>-mention list: <c>MySQL ·</c>, where it points, then its description. Pinned.</summary>
    public static string MentionNote(MySqlNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string where = "MySQL · " + config.Endpoint + (string.IsNullOrWhiteSpace(config.Database) ? "" : " / " + config.Database.Trim());
        return string.IsNullOrWhiteSpace(config.Description) ? where : where + " — " + config.Description.Trim();
    }

    /// <summary><c>mysql_connections</c>' whole answer: a count, then one line each, then the problems that kept any out.</summary>
    public static string Connections(MySqlCatalog catalog, string? defaultName, bool writes = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        if (catalog.Connections.Count == 0)
        {
            sb.Append(NoConnections);
        }
        else
        {
            var chosen = catalog.Find(null, defaultName);
            sb.Append(SqlText.Count(catalog.Connections.Count, "MySQL connection")).Append(" (every MySQL tool takes one by name in \"connection\"; the default is used when it is left out):");
            foreach (var connection in catalog.Connections)
            {
                sb.Append('\n').Append(ConnectionLine(connection, ReferenceEquals(connection, chosen), writes));
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
    public const string SelectInto = "Error: SELECT … INTO writes a file or a variable; the MySQL tools only read — drop the INTO";
    public static string Unterminated(string what, int line, int column) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): a {what} never ends";
    public static string ExecutableComment(int line, int column) => $"Error: the SQL holds an executable comment (/*! … */, line {Invariant(line)}, column {Invariant(column)}), which the server runs as code; write it as plain SQL";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one SELECT per call (a WITH clause may lead it)";
    public static string NotASelect(string word) => $"Error: the SQL starts with {word}; the MySQL tools only read — send one SELECT (a WITH clause may lead it)";
    public static string Forbidden(string what) => $"Error: the SELECT uses {what}, which the MySQL tools refuse (it reaches outside this database or changes state a rollback cannot undo)";

    // ─── arguments ──────────────────────────────────────────────────────────────

    public const string NoTable = "Error: give the table or view in \"table\" (database.name, e.g. shop.orders)";
    public const string NoPattern = "Error: give the column name (or part of it) to look for in \"pattern\"";
    public static string BadTable(string table) => $"Error: '{table}' is not a table name (name or database.name; `backticks` around a name with dots)";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5, \"name\": \"x\"}} for @id and @name (got {Clip(raw, 200)})";
    public static string TableNotFound(string table, string where) => $"Error: no table or view '{table}' visible in {where}; mysql_tables lists them";
    public static string TableAmbiguous(string table, IEnumerable<string> candidates) => $"Error: '{table}' matches more than one table or view; give the database: {string.Join(", ", candidates)}";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoConnections = "Error: no MySQL connection is defined; the user adds one to mysql.json (the MySQL tab of /tools)";
    public static string UnknownConnection(string name, string names) => $"Error: no MySQL connection is named '{name}'; the connections are {names}";
    public static string ConnectFailed(string connection, string detail) => $"Error: could not connect to {connection}: {detail}";
    public static string Timeout(string connection, string seconds) => $"Error: the query on {connection} ran past {seconds} s and was stopped; narrow it (WHERE, LIMIT, fewer joins)";
    public static string Failed(string connection, string detail) => $"Error: the server refused the SQL ({connection}): {detail}";
    public static string ServerError(int number, string message) => $"MySQL {Invariant(number)}: {message}";
    public static string ConnectFailedLogLine(string connection, int number, string detail) => $"{connection} did not connect (error {Invariant(number)}): {detail}";

    /// <summary>The sentence for a run that did not return rows.</summary>
    public static string Error(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Outcome switch
        {
            SqlOutcome.NoConnections => NoConnections,
            SqlOutcome.UnknownConnection => UnknownConnection(run.Connection, run.Detail),
            SqlOutcome.ConnectFailed => ConnectFailed(run.Connection, run.Detail),
            SqlOutcome.Timeout => Timeout(run.Connection, run.Detail),
            SqlOutcome.ReadOnlyConnection => ServerWriteText.ReadOnlyConnection(MySqlStatementKinds.Family, run.Connection),
            _ => Failed(run.Connection, run.Detail),
        };
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>A value the client cannot read: its type, and how to read it anyway.</summary>
    public static string Unreadable(string type) => $"({type}: select it converted to text — CAST(… AS CHAR), ST_AsText)";

    /// <summary><c>mysql_query</c>'s answer: <c>N rows × M columns from connection/database (T ms)</c>, a sentence when a cap cut it, and the table.</summary>
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

    /// <summary>A catalog listing's answer: a header naming what and where, then the table (cut by the text cap, said so).</summary>
    public static string Listing(string singular, string plural, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = Set(run, 0);
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
    /// <c>mysql_describe</c>'s answer: the object's name and kind (its comment on the next line), then its columns (the type as
    /// declared, <c>varchar(100)</c>, <c>decimal(10,2) unsigned</c>; <c>auto_increment</c> and generated columns marked; a
    /// description column only when one has a comment), then its keys out and in, its indexes, its CHECK constraints and its
    /// triggers — each section only when it has a line. The result sets are read by position (<see cref="MySqlCatalogQueries.Describe"/>).
    /// </summary>
    public static string Describe(string database, string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var columns = Set(run, 0);
        var sb = new StringBuilder();
        sb.Append(database).Append('.').Append(name).Append(" (").Append(kind).Append(", ").Append(SqlText.Count(columns.Rows.Count, "column"))
            .Append(") in ").Append(run.Connection).Append('\n');
        if (Set(run, 3).Rows is [var described, ..] && At(described, 0) is { Length: > 0 } comment && comment != SqlText.Null)
        {
            sb.Append(OneLine(comment)).Append('\n');
        }

        // column, type, nullable, extra, default, pk, description
        bool descriptions = columns.Rows.Any(c => At(c, 6) != SqlText.Null);
        sb.Append(descriptions ? "\n| column | type | null | key | default | description |\n|---|---|---|---|---|---|\n" : "\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        int keyColumns = columns.Rows.Count(r => At(r, 5) != SqlText.Null);
        foreach (var c in columns.Rows)
        {
            string extra = At(c, 3).ToLowerInvariant();
            string type = At(c, 1) + (extra.Contains("auto_increment", StringComparison.Ordinal) ? " auto_increment" : "") + (extra.Contains("generated", StringComparison.Ordinal) ? " generated" : "");
            string key = At(c, 5) == SqlText.Null ? "" : "PK" + (keyColumns > 1 ? " " + c[5] : "");
            string def = At(c, 4) == SqlText.Null ? "" : OneLine(c[4]);
            sb.Append("| ").Append(SqlText.TableCell(c[0])).Append(" | ").Append(SqlText.TableCell(type)).Append(" | ").Append(At(c, 2) == "NO" ? "no" : "yes")
                .Append(" | ").Append(key).Append(" | ").Append(SqlText.TableCell(def)).Append(" |");
            if (descriptions)
            {
                sb.Append(' ').Append(At(c, 6) == SqlText.Null ? "" : SqlText.TableCell(c[6])).Append(" |");
            }

            sb.Append('\n');
        }

        if (Set(run, 1).Rows.Count > 0)
        {
            sb.Append("\nForeign keys:\n");
            foreach (var k in Set(run, 1).Rows)
            {
                // direction, constraint, from_table, from_column, to_table, to_column
                sb.Append("- ").Append(k[0] == "out" ? "out" : "in ").Append(": ").Append(k[2]).Append('.').Append(k[3]).Append(" -> ").Append(k[4]).Append('.').Append(k[5]).Append(" (").Append(k[1]).Append(")\n");
            }
        }

        if (Set(run, 2).Rows.Count > 0)
        {
            sb.Append("\nIndexes:\n");
            foreach (var i in Set(run, 2).Rows)
            {
                // index, kind, non_unique, keys
                string flags = At(i, 0) == "PRIMARY" ? "primary key, " : At(i, 2) == "0" ? "unique, " : "";
                sb.Append("- ").Append(i[0]).Append(" (").Append(flags).Append(At(i, 1).ToLowerInvariant()).Append("): ").Append(Blank(At(i, 3))).Append('\n');
            }
        }

        if (Set(run, 4).Rows.Count > 0)
        {
            sb.Append("\nCheck constraints:\n");
            foreach (var k in Set(run, 4).Rows)
            {
                // constraint, definition
                sb.Append("- ").Append(k[0]).Append(": ").Append(OneLine(At(k, 1))).Append('\n');
            }
        }

        if (Set(run, 5).Rows.Count > 0)
        {
            sb.Append("\nTriggers:\n");
            foreach (var t in Set(run, 5).Rows)
            {
                // trigger, timing, event
                sb.Append("- ").Append(t[0]).Append(" (").Append(At(t, 1).ToLowerInvariant()).Append(' ').Append(At(t, 2).ToLowerInvariant()).Append(")\n");
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary><c>mysql_columns</c>' answer: the rows as a listing, nullability a word, the description column only when a column has one.</summary>
    public static string Columns(string pattern, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, kind, column, type, nullable, description
        var found = Set(run, 0);
        var rows = found.Rows.Select(r => new[] { r[0], r[1], r[2], At(r, 3), At(r, 4) == "NO" ? "no" : "yes", At(r, 5) }).ToList();
        var grid = SqlText.WithoutEmptyColumn(new SqlGrid(["table", "kind", "column", "type", "null", "description"], rows, found.More), "description");
        string listing = Listing("column", "columns", run with { Grids = [grid] }, maxChars);
        return grid.Rows.Count == 0 ? listing + $" named like '{pattern}'" : listing;
    }

    /// <summary>
    /// <c>mysql_indexes</c>' answer: a header naming the scope, one table row per index — kind and flags, key columns, the
    /// cardinality estimate — and, when <paramref name="usage"/> was read (<c>performance_schema</c>), its reads and writes since
    /// the server started, an index nothing has read (not a key's) marked <see cref="UnusedMarker"/>. <paramref name="usageError"/>
    /// says why usage is missing.
    /// </summary>
    public static string Indexes(string scope, SqlRun run, SqlGrid? usage, string? usageError, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, index, kind, non_unique, keys, cardinality, database, name
        var indexes = Set(run, 0);
        var used = new Dictionary<(string, string, string), string[]>();
        foreach (var u in usage?.Rows ?? [])
        {
            // database, table, index, reads, writes
            used[(u[0], u[1], u[2])] = u;
        }

        var sb = new StringBuilder();
        sb.Append(SqlText.Count(indexes.Rows.Count, "index", "indexes")).Append(indexes.More ? "+" : "").Append(scope.Length > 0 ? " " + scope : "").Append(" in ").Append(run.Connection);
        if (usage is not null)
        {
            sb.Append(" (usage since the server started)");
        }

        sb.Append('\n');
        if (usageError is not null)
        {
            sb.Append(UsageUnavailable(usageError)).Append('\n');
        }

        if (indexes.Rows.Count > 0)
        {
            sb.Append(usage is not null
                ? "\n| table | index | kind | keys | cardinality | reads | writes |\n|---|---|---|---|---|---|---|\n"
                : "\n| table | index | kind | keys | cardinality |\n|---|---|---|---|---|\n");
            int shown = 0;
            foreach (var i in indexes.Rows)
            {
                var line = new StringBuilder();
                bool primary = At(i, 1) == "PRIMARY";
                string kind = At(i, 2).ToLowerInvariant() + (primary ? " PK" : At(i, 3) == "0" ? " unique" : "");
                line.Append("| ").Append(SqlText.TableCell(i[0])).Append(" | ").Append(SqlText.TableCell(i[1])).Append(" | ").Append(kind)
                    .Append(" | ").Append(SqlText.TableCell(Blank(At(i, 4)))).Append(" | ").Append(Blank(At(i, 5))).Append(" |");
                if (usage is not null)
                {
                    bool recorded = used.TryGetValue((At(i, 6), At(i, 7), i[1]), out var u);
                    string reads = recorded ? At(u!, 3) : "0";
                    bool unread = reads == "0" && !primary && At(i, 3) != "0";
                    line.Append(' ').Append(reads).Append(unread ? " " + UnusedMarker : "").Append(" | ").Append(recorded ? At(u!, 4) : "0").Append(" |");
                }

                line.Append('\n');
                if (sb.Length + line.Length > maxChars)
                {
                    sb.Append($"[… the first {Invariant(shown)} shown; give a table or a database to narrow it]\n");
                    break;
                }

                sb.Append(line);
                shown++;
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary>The mark on an index nothing has read since the server started (a key's index is never marked). Pinned.</summary>
    public const string UnusedMarker = "(no reads since restart)";

    /// <summary>The line when <c>performance_schema</c> refused the account, or is off (MariaDB's default). Pinned.</summary>
    public static string UsageUnavailable(string detail) => $"Usage not shown: performance_schema is off or the account may not read it: {detail}";

    /// <summary><c>mysql_relationships</c>' answer: one line per column pair, <c>from.col -> to.col (constraint)</c>.</summary>
    public static string Relationships(string? scope, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = Set(run, 0);
        string touching = string.IsNullOrWhiteSpace(scope) ? "" : $" touching {scope}";
        if (grid.Rows.Count == 0)
        {
            return $"No foreign keys{touching} in {run.Connection}";
        }

        var sb = new StringBuilder();
        sb.Append(SqlText.Count(grid.Rows.Count, "foreign-key column pair")).Append(grid.More ? "+" : "").Append(touching).Append(" in ").Append(run.Connection).Append(" (join on these):");
        int shown = 0;
        foreach (var r in grid.Rows)
        {
            // constraint, from_table, from_column, to_table, to_column
            string line = $"\n- {r[1]}.{r[2]} -> {r[3]}.{r[4]} ({r[0]})";
            if (sb.Length + line.Length > maxChars)
            {
                break;
            }

            sb.Append(line);
            shown++;
        }

        if (grid.More || shown < grid.Rows.Count)
        {
            sb.Append($"\n[… the first {Invariant(shown)} shown; give a table or a database to narrow it]");
        }

        return sb.ToString();
    }

    // ─── the wizard's test ──────────────────────────────────────────────────────

    /// <summary>The wizard's test line: who it signed in as, the server and its version. Pinned.</summary>
    public static string TestOk(string name, string user, string version) => $"Connected to '{name}' as {user}, {version}.";

    /// <summary>
    /// The wizard's warning when the account could change data (the tools will not; a SELECT-only account is the real guard). Pinned.
    /// A <c>readwrite</c> connection (2026-10-05) is told what may then change it: <c>mysql_execute</c>, each change allowed by the user.
    /// </summary>
    public static string CanWrite(IReadOnlyList<string> what, bool readWrite = false) =>
        $"This account can change data ({string.Join("; ", what.Take(3))}{(what.Count > 3 ? $"; and {Invariant(what.Count - 3)} more" : "")}); " +
        (readWrite
            ? "as readwrite, mysql_execute may use those powers under MySQL mode read-write, each change allowed by you; the account is still the real guard."
            : "the MySQL tools only read, but a SELECT-only account is the real guard.");

    /// <summary>The privileges that only read (or only connect); every other one in a <c>SHOW GRANTS</c> line can change something.</summary>
    public static readonly IReadOnlySet<string> ReadingPrivileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "USAGE", "SELECT", "SHOW VIEW", "SHOW DATABASES", "PROCESS", "REPLICATION CLIENT", "EXECUTE", "REFERENCES",
    };

    /// <summary>
    /// What <c>SHOW GRANTS</c>' lines say the account could change: each grant's writing privileges with what they are on
    /// (<c>ALL PRIVILEGES ON `shop`.*</c>, <c>INSERT, UPDATE ON `shop`.`orders`</c>); a role grant or a read-only one says
    /// nothing. Pure.
    /// </summary>
    public static IReadOnlyList<string> WritePowers(IEnumerable<string> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var powers = new List<string>();
        foreach (string grant in grants)
        {
            int on = grant.IndexOf(" ON ", StringComparison.OrdinalIgnoreCase);
            int to = grant.IndexOf(" TO ", StringComparison.OrdinalIgnoreCase);
            if (!grant.StartsWith("GRANT ", StringComparison.OrdinalIgnoreCase) || on < 0 || to < on)
            {
                continue;
            }

            var writing = grant[6..on].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0 && !ReadingPrivileges.Contains(p)).ToList();
            if (writing.Count > 0)
            {
                powers.Add(string.Join(", ", writing) + " ON " + grant[(on + 4)..to].Trim());
            }
        }

        return powers;
    }

    // ─── shared ─────────────────────────────────────────────────────────────────

    /// <summary>Where a run was: <c>connection/database</c>, or the connection alone with no database in force.</summary>
    public static string Where(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Database.Length > 0 ? run.Connection + "/" + run.Database : run.Connection;
    }

    private static string Blank(string cell) => cell == SqlText.Null ? "" : cell;

    private static string OneLine(string text) => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static SqlGrid Set(SqlRun run, int index) => run.Grids.Count > index ? run.Grids[index] : new SqlGrid([], [], false);

    private static string At(string[] row, int index) => row.Length > index ? row[index] : SqlText.Null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Millis(TimeSpan elapsed) => Invariant((long)Math.Round(elapsed.TotalMilliseconds)) + " ms";

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
