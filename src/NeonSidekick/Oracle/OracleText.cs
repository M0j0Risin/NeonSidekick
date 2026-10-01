using System.Globalization;
using System.Text;
using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// The Oracle tools' wording and formatting (2026-09-30), the <see cref="SqlText"/> part for Oracle, pure and pinned:
/// the <c>oracle.json</c> problems, the gate's refusals, the outcomes and the results. The engine-neutral pieces — a
/// cell's text, a Markdown table, a count, the transcript note — are <see cref="SqlText"/>'s own. Every error starts
/// <c>Error:</c>; a place is written <c>connection/SCHEMA</c>. Invariant culture throughout.
/// </summary>
public static class OracleText
{
    // ─── oracle.json ────────────────────────────────────────────────────────────

    public const string NoDataSource = "no \"dataSource\" is given (host:port/service, or a (DESCRIPTION=…))";
    public const string NoUser = "no \"user\" is given";
    public static string SysRefused(string user) => $"\"user\" is '{user}'; SYS (or any AS SYSDBA-style sign-in) is not held to a read-only transaction, so the Oracle tools refuse it — use a read-only account";
    public static string BadSchemaKey(string schema) => $"\"schema\" is '{schema}', which is no Oracle name";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on the Oracle tab of /tools (Oracle set password)";
    public static string NoCredential(string target) => $"no password in Windows Credential Manager for {target}; set it on the Oracle tab of /tools, or: cmdkey /generic:{target} /user:<user> /pass";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's oracle.json was checked for plain passwords: {detail}";
    public const string NoPasswordConnections = "No connection in oracle.json yet; add one first (Oracle add connection).";

    /// <summary>One connection as <c>oracle_connections</c> lists it: the name, where, the user, the schema, the description — never the password.</summary>
    public static string ConnectionLine(OracleNamedConnection connection, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string schema = string.IsNullOrWhiteSpace(config.Schema) ? "" : $", schema {config.Schema.Trim()}";
        string line = $"- {connection.Name}{(isDefault ? " (default)" : "")}: {config.DataSource?.Trim()}, user {config.User?.Trim()}{schema}";
        return string.IsNullOrWhiteSpace(config.Description) ? line : line + " — " + config.Description.Trim();
    }

    /// <summary>The closing line of <c>oracle_connections</c> while the profile hides some: how many, never which. Pinned.</summary>
    public static string HiddenConnections(int count) =>
        count == 1
            ? "1 more connection in oracle.json is switched off for this profile (the Oracle tab of /tools)."
            : $"{Invariant(count)} more connections in oracle.json are switched off for this profile (the Oracle tab of /tools).";

    /// <summary>A connection's note on the <c>%</c>-mention list: <c>Oracle ·</c>, where it points, then its description. Pinned.</summary>
    public static string MentionNote(OracleNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string where = "Oracle · " + config.DataSource?.Trim() + (string.IsNullOrWhiteSpace(config.Schema) ? "" : " / " + config.Schema.Trim());
        return string.IsNullOrWhiteSpace(config.Description) ? where : where + " — " + config.Description.Trim();
    }

    /// <summary><c>oracle_connections</c>' whole answer: a count, then one line each, then the problems that kept any out.</summary>
    public static string Connections(OracleCatalog catalog, string? defaultName)
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
            sb.Append(SqlText.Count(catalog.Connections.Count, "Oracle connection")).Append(" (every Oracle tool takes one by name in \"connection\"; the default is used when it is left out):");
            foreach (var connection in catalog.Connections)
            {
                sb.Append('\n').Append(ConnectionLine(connection, ReferenceEquals(connection, chosen)));
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
    public const string SelectInto = "Error: SELECT … INTO belongs to PL/SQL; the Oracle tools run one plain SELECT — drop the INTO";
    public static string Unterminated(string what, int line, int column) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): a {what} never ends";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one SELECT per call (a WITH clause may lead it)";
    public static string NotASelect(string word) => $"Error: the SQL starts with {word}; the Oracle tools only read — send one SELECT (a WITH clause may lead it)";
    public static string Forbidden(string what) => $"Error: the SELECT uses {what}, which the Oracle tools refuse (it reaches outside this database or changes state a rollback cannot undo)";

    // ─── arguments ──────────────────────────────────────────────────────────────

    public const string NoTable = "Error: give the table or view in \"table\" (SCHEMA.NAME, e.g. HR.EMPLOYEES)";
    public const string NoPattern = "Error: give the column name (or part of it) to look for in \"pattern\"";
    public static string BadTable(string table) => $"Error: '{table}' is not a table name (NAME or SCHEMA.NAME; \"quoted\" keeps the case)";
    public static string BadSchema(string schema) => $"Error: '{schema}' is not a schema name (oracle_schemas lists them)";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5, \"name\": \"x\"}} for :id and :name (got {Clip(raw, 200)})";
    public static string BadParamName(string name) => $"Error: '{name}' is no parameter name; use letters, digits and _ (bound as :name)";
    public static string TableNotFound(string table, string connection, string schema) => $"Error: no table or view '{table}' visible in {connection}/{schema}; oracle_tables lists them";
    public static string TableAmbiguous(string table, IEnumerable<string> candidates) => $"Error: '{table}' names more than one; give the schema: {string.Join(", ", candidates)}";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoConnections = "Error: no Oracle connection is defined; the user adds one to oracle.json (the Oracle tab of /tools)";
    public static string UnknownConnection(string name, string names) => $"Error: no Oracle connection is named '{name}'; the connections are {names}";
    public static string ConnectFailed(string connection, string detail) => $"Error: could not connect to {connection}: {detail}";
    public static string Timeout(string connection, string seconds) => $"Error: the query on {connection} ran past {seconds} s and was stopped; narrow it (WHERE, FETCH FIRST n ROWS ONLY, fewer joins)";
    public static string Failed(string connection, string detail) => $"Error: the server refused it ({connection}): {detail}";
    public static string ConnectFailedLogLine(string connection, int number, string detail) => $"{connection} did not connect (ORA-{number.ToString("D5", CultureInfo.InvariantCulture)}): {detail}";
    public static string SessionReadOnlySkippedLogLine(string connection, string version) => $"{connection} is Oracle {version}: no session READ_ONLY before 23ai, so a read-only transaction and the gate stand alone";

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
            _ => Failed(run.Connection, run.Detail),
        };
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>A value the client cannot read (an object type, <c>SDO_GEOMETRY</c>, <c>XMLTYPE</c>): its type, and how to read it anyway.</summary>
    public static string Unreadable(string type) => $"({type}: select it converted to text — TO_CHAR, XMLSERIALIZE, JSON_SERIALIZE, SDO_UTIL.TO_WKTGEOMETRY)";

    /// <summary>
    /// <c>oracle_query</c>'s answer: <c>N rows × M columns from connection/SCHEMA (T ms)</c>, a sentence when the row cap or
    /// the text cap cut it, and the table.
    /// </summary>
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
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown, more exist (narrow it with WHERE or FETCH FIRST, or raise max_rows)");
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
    /// <c>oracle_describe</c>'s answer: the object's name and kind (its comment on the next line), then its columns (the type
    /// written as a declaration would, <c>VARCHAR2(100 CHAR)</c>, <c>NUMBER(12,2)</c>; a description column only when one
    /// has a comment), then its keys out and in, its indexes, its CHECK constraints (the generated <c>IS NOT NULL</c> ones
    /// left out: the null column says it) and its triggers — each section only when it has a line. The result sets are read
    /// by position (<see cref="OracleCatalogQueries.Describe"/>); a shorter run, or a shorter row, reads as empty.
    /// </summary>
    public static string Describe(string owner, string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var columns = Set(run, 0);
        var sb = new StringBuilder();
        sb.Append(owner).Append('.').Append(name).Append(" (").Append(kind).Append(", ").Append(SqlText.Count(columns.Rows.Count, "column"))
            .Append(") in ").Append(run.Connection).Append('\n');
        if (Set(run, 3).Rows is [var described, ..] && At(described, 0) != SqlText.Null)
        {
            sb.Append(OneLine(At(described, 0))).Append('\n');
        }

        // column, type, data_length, data_precision, data_scale, char_length, char_used, nullable, identity_column, virtual_column, default, pk, description
        bool descriptions = columns.Rows.Any(c => At(c, 12) != SqlText.Null);
        sb.Append(descriptions ? "\n| column | type | null | key | default | description |\n|---|---|---|---|---|---|\n" : "\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        int keyColumns = columns.Rows.Count(r => At(r, 11) != SqlText.Null);
        foreach (var c in columns.Rows)
        {
            string type = TypeName(At(c, 1), At(c, 2), At(c, 3), At(c, 4), At(c, 5), At(c, 6))
                + (At(c, 8) == "YES" ? " identity" : "") + (At(c, 9) == "YES" ? " virtual" : "");
            string key = At(c, 11) == SqlText.Null ? "" : "PK" + (keyColumns > 1 ? " " + c[11] : "");
            string def = At(c, 10) == SqlText.Null ? "" : OneLine(c[10]);
            sb.Append("| ").Append(SqlText.TableCell(c[0])).Append(" | ").Append(type).Append(" | ").Append(At(c, 7) == "N" ? "no" : "yes")
                .Append(" | ").Append(key).Append(" | ").Append(SqlText.TableCell(def)).Append(" |");
            if (descriptions)
            {
                sb.Append(' ').Append(At(c, 12) == SqlText.Null ? "" : SqlText.TableCell(c[12])).Append(" |");
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
                // index, kind, uniqueness, constraint, keys, status
                string flags = At(i, 3) == "P" ? "primary key, " : At(i, 3) == "U" ? "unique constraint, " : At(i, 2) == "UNIQUE" ? "unique, " : "";
                string status = At(i, 5) is "VALID" or "N/A" or "NULL" ? "" : ", " + At(i, 5).ToLowerInvariant();
                sb.Append("- ").Append(i[0]).Append(" (").Append(flags).Append(Kind(At(i, 1))).Append(status).Append("): ").Append(Blank(At(i, 4))).Append('\n');
            }
        }

        var checks = Set(run, 4).Rows.Where(k => !IsNotNullCheck(k)).ToList();
        if (checks.Count > 0)
        {
            sb.Append("\nCheck constraints:\n");
            foreach (var k in checks)
            {
                // constraint, definition, status, generated
                sb.Append("- ").Append(k[0]).Append(": ").Append(OneLine(At(k, 1))).Append(At(k, 2) == "DISABLED" ? " (disabled)" : "").Append('\n');
            }
        }

        if (Set(run, 5).Rows.Count > 0)
        {
            sb.Append("\nTriggers:\n");
            foreach (var t in Set(run, 5).Rows)
            {
                // trigger, trigger_type, triggering_event, status
                sb.Append("- ").Append(t[0]).Append(" (").Append(Kind(At(t, 1))).Append(' ').Append(Blank(At(t, 2)).ToLowerInvariant())
                    .Append(At(t, 3) == "DISABLED" ? ", disabled" : "").Append(")\n");
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary>A generated <c>"COL" IS NOT NULL</c> check: what every NOT NULL column carries, shown already by the null column.</summary>
    private static bool IsNotNullCheck(string[] check) =>
        At(check, 3) == "GENERATED NAME" && At(check, 1).TrimEnd().EndsWith("IS NOT NULL", StringComparison.OrdinalIgnoreCase) && !At(check, 1).Contains(" AND ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>oracle_columns</c>' answer: the <see cref="OracleCatalogQueries.Columns"/> rows as a listing, the type parts made
    /// one declaration (<see cref="TypeName"/>), nullability a word, the description column only when a column has one.
    /// </summary>
    public static string Columns(string pattern, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, kind, column, type, data_length, data_precision, data_scale, char_length, char_used, nullable, description
        var found = Set(run, 0);
        var rows = found.Rows.Select(r => new[] { r[0], r[1], r[2], TypeName(At(r, 3), At(r, 4), At(r, 5), At(r, 6), At(r, 7), At(r, 8)), At(r, 9) == "N" ? "no" : "yes", At(r, 10) }).ToList();
        var grid = SqlText.WithoutEmptyColumn(new SqlGrid(["table", "kind", "column", "type", "null", "description"], rows, found.More), "description");
        string listing = Listing("column", "columns", run with { Grids = [grid] }, maxChars);
        return grid.Rows.Count == 0 ? listing + $" named like '{pattern}'" : listing;
    }

    /// <summary>
    /// <c>oracle_indexes</c>' answer: a header naming the scope, one table row per index — kind and flags, key columns, status
    /// and visibility, the optimizer's row and distinct-key counts, when last analyzed — and, when <paramref name="usage"/> was
    /// read (<c>DBA_INDEX_USAGE</c>), its accesses and last use, an index with none recorded (not a key's) marked
    /// <see cref="UnusedMarker"/>. <paramref name="usageError"/> says why usage is missing. Oracle keeps no missing-index
    /// hints to show.
    /// </summary>
    public static string Indexes(string scope, SqlRun run, SqlGrid? usage, string? usageError, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, index, kind, uniqueness, constraint, keys, status, visibility, num_rows, distinct_keys, last_analyzed, owner
        var indexes = Set(run, 0);
        var used = new Dictionary<(string, string), string[]>();
        foreach (var u in usage?.Rows ?? [])
        {
            // owner, name, total_access_count, total_exec_count, total_rows_returned, last_used
            used[(u[0], u[1])] = u;
        }

        var sb = new StringBuilder();
        sb.Append(SqlText.Count(indexes.Rows.Count, "index", "indexes")).Append(indexes.More ? "+" : "").Append(scope.Length > 0 ? " " + scope : "").Append(" in ").Append(run.Connection);
        if (usage is not null)
        {
            sb.Append(" (usage as DBA_INDEX_USAGE tracked it)");
        }

        sb.Append('\n');
        if (usageError is not null)
        {
            sb.Append(UsageUnavailable(usageError)).Append('\n');
        }

        if (indexes.Rows.Count > 0)
        {
            sb.Append(usage is not null
                ? "\n| table | index | kind | keys | status | rows | distinct | analyzed | accesses | last used |\n|---|---|---|---|---|---|---|---|---|---|\n"
                : "\n| table | index | kind | keys | status | rows | distinct | analyzed |\n|---|---|---|---|---|---|---|---|\n");
            int shown = 0;
            foreach (var i in indexes.Rows)
            {
                var line = new StringBuilder();
                string status = (At(i, 6) is "VALID" or "N/A" ? "" : At(i, 6).ToLowerInvariant()) + (At(i, 7) == "INVISIBLE" ? (At(i, 6) is "VALID" or "N/A" ? "" : ", ") + "invisible" : "");
                line.Append("| ").Append(SqlText.TableCell(i[0])).Append(" | ").Append(SqlText.TableCell(i[1])).Append(" | ").Append(IndexKind(i))
                    .Append(" | ").Append(SqlText.TableCell(Blank(At(i, 5)))).Append(" | ").Append(status)
                    .Append(" | ").Append(Blank(At(i, 8))).Append(" | ").Append(Blank(At(i, 9))).Append(" | ").Append(Blank(At(i, 10))).Append(" |");
                if (usage is not null)
                {
                    bool recorded = used.TryGetValue((At(i, 11), i[1]), out var u);
                    string accesses = recorded ? At(u!, 2) : "0";
                    bool key = At(i, 4) is "P" or "U";
                    line.Append(' ').Append(accesses).Append(!recorded && !key ? " " + UnusedMarker : "").Append(" | ").Append(recorded ? Blank(At(u!, 5)) : "").Append(" |");
                }

                line.Append('\n');
                if (sb.Length + line.Length > maxChars)
                {
                    sb.Append($"[… the first {Invariant(shown)} shown; give a table or a schema to narrow it]\n");
                    break;
                }

                sb.Append(line);
                shown++;
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary>The mark on an index <c>DBA_INDEX_USAGE</c> has no use of (it samples; a key's index is never marked). Pinned.</summary>
    public const string UnusedMarker = "(no use recorded)";

    /// <summary>The line when <c>DBA_INDEX_USAGE</c> refused the account. Pinned.</summary>
    public static string UsageUnavailable(string detail) => $"Usage not shown: the account may not read DBA_INDEX_USAGE (it needs SELECT_CATALOG_ROLE or SELECT ANY DICTIONARY): {detail}";

    /// <summary>The kind and flags of one <see cref="OracleCatalogQueries.Indexes"/> row: <c>normal PK</c>, <c>normal unique</c>, <c>bitmap</c>.</summary>
    private static string IndexKind(string[] i)
    {
        string flag = At(i, 4) == "P" ? " PK" : At(i, 4) == "U" ? " unique constraint" : At(i, 3) == "UNIQUE" ? " unique" : "";
        return Kind(At(i, 2)) + flag;
    }

    /// <summary><c>oracle_relationships</c>' answer: one line per column pair, <c>from.col -> to.col (constraint)</c>.</summary>
    public static string Relationships(string? scope, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = Set(run, 0);
        string where = run.Connection;
        string touching = string.IsNullOrWhiteSpace(scope) ? "" : $" touching {scope}";
        if (grid.Rows.Count == 0)
        {
            return $"No foreign keys{touching} in {where}";
        }

        var sb = new StringBuilder();
        sb.Append(SqlText.Count(grid.Rows.Count, "foreign-key column pair")).Append(grid.More ? "+" : "").Append(touching).Append(" in ").Append(where).Append(" (join on these):");
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
            sb.Append($"\n[… the first {Invariant(shown)} shown; give a table or a schema to narrow it]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// A column's type as a declaration writes it, from the dictionary's parts: <c>VARCHAR2(100 CHAR)</c> (the length in
    /// characters when it was declared so), <c>RAW(16)</c>, <c>NUMBER(12,2)</c>, <c>NUMBER(10)</c>, a bare <c>NUMBER</c>,
    /// <c>FLOAT(126)</c>; the rest (dates, timestamps, LOBs, BOOLEAN) as the dictionary names them.
    /// </summary>
    public static string TypeName(string type, string dataLength, string precision, string scale, string charLength, string charUsed)
    {
        ArgumentNullException.ThrowIfNull(type);
        string upper = type.ToUpperInvariant();
        static bool Has(string cell) => cell != SqlText.Null && cell.Length > 0;
        return upper switch
        {
            "VARCHAR2" or "CHAR" when charUsed == "C" => $"{type}({charLength} CHAR)",
            "VARCHAR2" or "CHAR" => $"{type}({dataLength})",
            "NVARCHAR2" or "NCHAR" => $"{type}({(Has(charLength) ? charLength : dataLength)})",
            "RAW" => $"{type}({dataLength})",
            "NUMBER" when Has(precision) && Has(scale) && scale != "0" => $"{type}({precision},{scale})",
            "NUMBER" when Has(precision) => $"{type}({precision})",
            "NUMBER" when Has(scale) && scale == "0" => "INTEGER",
            "FLOAT" when Has(precision) => $"{type}({precision})",
            _ => type,
        };
    }

    // ─── the check mode and the wizard's test ───────────────────────────────────

    /// <summary>The wizard's test line: who it signed in as, where, the server's version. Pinned.</summary>
    public static string TestOk(string name, string user, string container, string version) => $"Connected to '{name}' as {user} ({container}), Oracle {version}.";

    /// <summary>The wizard's warning when the account could change data (the tools will not; a read-only account is the real guard). Pinned.</summary>
    public static string CanWrite(IReadOnlyList<string> what) =>
        $"This account can change data ({string.Join(", ", what.Take(4))}{(what.Count > 4 ? $", and {Invariant(what.Count - 4)} more" : "")}); the Oracle tools only read, but a read-only account is the real guard.";

    // ─── shared ─────────────────────────────────────────────────────────────────

    /// <summary>Where a run was: <c>connection/SCHEMA</c>.</summary>
    public static string Where(SqlRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Database.Length > 0 ? run.Connection + "/" + run.Database : run.Connection;
    }

    private static string Kind(string text) => text.ToLowerInvariant().Replace('_', ' ');

    private static string Blank(string cell) => cell == SqlText.Null ? "" : cell;

    private static string OneLine(string text) => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static SqlGrid Set(SqlRun run, int index) => run.Grids.Count > index ? run.Grids[index] : new SqlGrid([], [], false);

    private static string At(string[] row, int index) => row.Length > index ? row[index] : SqlText.Null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Millis(TimeSpan elapsed) => Invariant((long)Math.Round(elapsed.TotalMilliseconds)) + " ms";

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
