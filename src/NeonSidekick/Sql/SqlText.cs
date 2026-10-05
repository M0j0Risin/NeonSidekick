using System.Globalization;
using System.Text;

namespace NeonSidekick.Sql;

/// <summary>
/// The SQL tools' wording and formatting (2026-09-23), pure and pinned: the <c>sql.json</c> problems, the gate's
/// refusals, the outcomes, the cells, and the results — a header line that stands alone (the transcript shows
/// only it, <see cref="Note"/>) over a Markdown table. Every error starts <c>Error:</c>. Invariant culture throughout.
/// </summary>
public static class SqlText
{
    /// <summary>How long one cell may be before it is clipped.</summary>
    public const int MaxCellChars = 400;

    /// <summary>How many bytes of a binary value are shown as hex.</summary>
    public const int MaxBinaryBytes = 32;

    /// <summary>A NULL cell.</summary>
    public const string Null = "NULL";

    // ─── sql.json ───────────────────────────────────────────────────────────────

    public const string NoServer = "no \"server\" is given";
    public const string NoUser = "a sql login needs a \"user\" (or \"auth\": \"windows\")";
    public const string BlankName = "a connection has a blank name";
    public static string BadAuth(string word) => $"\"auth\" is '{word}'; it must be sql, windows or runas";
    public static string BadPasswordStore(string word) => $"\"passwordStore\" is '{word}'; it must be file or credman";
    public const string CredmanWithWindows = "\"passwordStore\": \"credman\" needs a password to keep; windows sign-in has none (use runas to sign in as another account)";
    public static string RunAsNeedsDomain(string account) => $"runas needs the Windows account as DOMAIN\\name or name@domain in \"user\" (got '{account}')";

    // ─── passwords (later on 2026-09-23) ───────────────────────────────────────

    public const string NotProtected = "the value is not one the app encrypted (dpapi:…)";
    public static string CannotDecrypt(string detail) => $"the password cannot be decrypted — it was saved by another Windows user or on another machine ({detail}); set it again on the SQL tab of /tools";
    public static string NoCredential(string target) => $"no password in Windows Credential Manager for {target}; set it on the SQL tab of /tools, or: cmdkey /generic:{target} /user:<account> /pass";
    public static string NoPassword(string name) => $"'{name}' has no password; set it on the SQL tab of /tools (SQL set password)";
    public static string ProfilesUnlistedLogLine(string root, string detail) => $"could not list the profiles in {root}, so only the home's sql.json was checked for plain passwords: {detail}";
    public static string EncryptedLogLine(string name, string path) => $"encrypted the password of '{name}' in {path}";
    public static string EncryptFailedLogLine(string name, string path, string detail) => $"could not encrypt the password of '{name}' in {path}, so it stays plain text there: {detail}";
    public static string RunAsLogLine(string name, string account) => $"{name} signs in as {account} (runas)";
    public static string PasswordSavedToFile(string name, string path) => $"Saved the password of '{name}', encrypted, in {path}.";
    public static string PasswordSavedToCredman(string name, string target) => $"Saved the password of '{name}' to Windows Credential Manager as {target}.";
    public static string PasswordSaveFailed(string name, string detail) => $"Could not save the password of '{name}': {detail}.";
    public static string ConnectionNotInFile(string name, string noun = "connection") => $"no {noun} '{name}' was found in the file to write to";
    public static string ConnectionAlreadyInFile(string name, string noun = "connection") => $"a {noun} named '{name}' is already in the file";
    public const string FileNotAnObject = "the file does not hold a JSON object";
    public const string ConnectionsNotAnObject = "\"connections\" in the file is not an object";

    /// <summary><see cref="ConnectionsNotAnObject"/> for a file whose entries sit under another key (<c>unc.json</c>'s <c>shares</c>, 2026-09-30). Pinned.</summary>
    public static string SectionNotAnObject(string section) => $"\"{section}\" in the file is not an object";
    public static string ConnectionAdded(string name, string path) => $"Added '{name}' to {path}.";
    public static string ConnectionAddFailed(string name, string detail) => $"Could not add '{name}': {detail}.";
    public static string ConnectionChanged(string name, string path) => $"Saved '{name}' in {path}.";   // 2026-10-05, the wizards' edit
    public static string ConnectionChangeFailed(string name, string detail) => $"Could not save '{name}': {detail}.";
    public const string NoPasswordConnections ="No connection in sql.json takes a password (sql or runas); add one first.";
    public static string BadEncrypt(string word) => $"\"encrypt\" is '{word}'; it must be strict, mandatory or optional";
    public static string BadConnectTimeout(int seconds, int max) => $"\"connectTimeoutSeconds\" is {Invariant(seconds)}; it must be 1 to {Invariant(max)}";
    public static string UnreadableFile(string detail) => $"the file cannot be read ({detail})";
    public static string ConnectionSource(string path, string name) => $"{path} ({name})";
    public static string ConfigProblemLogLine(string path, string detail) => $"{path} could not be read, so its connections are skipped: {detail}";

    /// <summary>One connection as <c>sql_connections</c> lists it: the name, where, how it signs in, the description — never the password.</summary>
    /// <remarks>A <c>readwrite</c> one says so (2026-10-05), and whether <paramref name="writes"/> (<c>SQL mode</c> read-write) lets it change.</remarks>
    public static string ConnectionLine(SqlNamedConnection connection, bool isDefault, bool writes = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string database = string.IsNullOrWhiteSpace(config.Database) ? "(the login's default database)" : config.Database.Trim();
        string login = config.IsWindows ? "windows sign-in" : config.IsRunAs ? $"windows sign-in as {config.User?.Trim()} (runas)" : $"sql login {config.User?.Trim()}";
        string line = $"- {connection.Name}{(isDefault ? " (default)" : "")}: {config.Server?.Trim()} / {database}, {login}{ServerWriteText.AccessNote(SqlStatementKinds.Family, config.IsReadWrite, writes)}";
        return string.IsNullOrWhiteSpace(config.Description) ? line : line + " — " + config.Description.Trim();
    }

    /// <summary>The closing line of <c>sql_connections</c> while the profile hides some (later on 2026-09-23): how many, never which. Pinned.</summary>
    public static string HiddenConnections(int count) =>
        count == 1
            ? "1 more connection in sql.json is switched off for this profile (the SQL tab of /tools)."
            : $"{Invariant(count)} more connections in sql.json are switched off for this profile (the SQL tab of /tools).";

    /// <summary>A connection's note on the <c>%</c>-mention list (later on 2026-09-23): where it points, then its description. Pinned.</summary>
    public static string MentionNote(SqlNamedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        string where = "SQL Server · " + config.Server?.Trim() + (string.IsNullOrWhiteSpace(config.Database) ? "" : " / " + config.Database.Trim());
        return string.IsNullOrWhiteSpace(config.Description) ? where : where + " — " + config.Description.Trim();
    }

    /// <summary><c>sql_connections</c>' whole answer: a count, then one line each, then the problems that kept any out.</summary>
    public static string Connections(SqlCatalog catalog, string? defaultName, bool writes = false)
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
            sb.Append(Count(catalog.Connections.Count, "SQL connection")).Append(" (every SQL tool takes one by name in \"connection\"; the default is used when it is left out):");
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
    public const string SelectInto = "Error: SELECT … INTO creates a table; the SQL tools only read — drop the INTO";
    public static string ParseError(int line, int column, string message) => $"Error: the SQL does not parse (line {Invariant(line)}, column {Invariant(column)}): {message}";
    public static string NotOneStatement(int count) => $"Error: the SQL is {Invariant(count)} statements; send exactly one SELECT per call (a CTE may lead it)";
    public static string NotASelect(string word) => $"Error: the SQL is a {word} statement; the SQL tools only read — send one SELECT (a CTE may lead it)";
    public static string Forbidden(string what) => $"Error: the SELECT uses {what}, which the SQL tools refuse (it reaches outside this server or changes state a rollback cannot undo)";

    // ─── arguments ──────────────────────────────────────────────────────────────

    public const string NoTable = "Error: give the table or view in \"table\" (schema.name, e.g. Sales.SalesOrderHeader)";
    public static string BadMissing(string raw) => $"Error: '{raw.Trim()}' is not true or false for 'missing'";
    public static string BadMaxRows(int min, int max) => $"Error: max_rows must be {Invariant(min)} to {Invariant(max)}";
    public static string BadParams(string raw) => $"Error: \"params\" must be one object of names and values, e.g. {{\"id\": 5, \"name\": \"x\"}} for @id and @name (got {Clip(raw, 200)})";
    public static string BadParamName(string name) => $"Error: '{name}' is not a valid parameter name; use letters, digits and _ (bound as @name)";
    public static string BadParamValue(string name) => $"Error: the value of '{name}' must be a string, a number, true, false or null";
    public static string TableNotFound(string table, string connection, string database) => $"Error: no table or view '{table}' in {connection}/{database}; sql_tables lists them";
    public static string TableAmbiguous(string table, IEnumerable<string> candidates) => $"Error: '{table}' matches more than one table or view; give the schema: {string.Join(", ", candidates)}";

    // ─── outcomes ───────────────────────────────────────────────────────────────

    public const string NoConnections = "Error: no SQL connection is defined; the user adds one to sql.json (the SQL tab of /tools)";
    public static string UnknownConnection(string name, string names) => $"Error: no SQL connection is named '{name}'; the connections are {names}";
    public static string ConnectFailed(string connection, string detail) => $"Error: could not connect to {connection}: {detail}";
    public static string Timeout(string connection, string seconds) => $"Error: the query on {connection} ran past {seconds} s and was stopped; narrow it (WHERE, TOP, fewer joins)";
    public static string Failed(string connection, string detail) => $"Error: the server refused the SQL ({connection}): {detail}";
    public static string ServerError(int number, int line, string message) => $"Msg {Invariant(number)}, line {Invariant(line)}: {message}";
    public static string ConnectFailedLogLine(string connection, string database, int number, string detail) => $"{connection}{(database.Length > 0 ? "/" + database : "")} did not connect (error {Invariant(number)}): {detail}";

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
            SqlOutcome.ReadOnlyConnection => ServerWriteText.ReadOnlyConnection(SqlStatementKinds.Family, run.Connection),
            _ => Failed(run.Connection, run.Detail),
        };
    }

    // ─── cells and tables ───────────────────────────────────────────────────────

    public static string UnnamedColumn(int ordinal) => $"(column {Invariant(ordinal + 1)})";

    /// <summary>A value the client cannot read (a CLR type): its type, and how to read it anyway.</summary>
    public static string Unreadable(string type) => $"({type}: select it with .ToString())";

    /// <summary>
    /// One value as text: dates in ISO order, a <see cref="decimal"/> with every digit, a float round-trippable,
    /// binary as <c>0x…</c> (the first <see cref="MaxBinaryBytes"/> bytes), a bit as true/false; clipped to
    /// <see cref="MaxCellChars"/>.
    /// </summary>
    public static string Cell(object? value)
    {
        string text = value switch
        {
            null or DBNull => Null,
            string s => s,
            bool b => b ? "true" : "false",
            DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),   // Npgsql's date (2026-10-04): ISO, never the invariant culture's MM/dd/yyyy
            TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
            byte[] bytes => Hex(bytes),
            Guid g => g.ToString("D"),
            double f => f.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString() ?? "",
        };

        return Clip(text, MaxCellChars);
    }

    private static string Hex(byte[] bytes) =>
        bytes.Length <= MaxBinaryBytes
            ? "0x" + Convert.ToHexString(bytes)
            : "0x" + Convert.ToHexString(bytes, 0, MaxBinaryBytes) + $"… ({Invariant(bytes.Length)} bytes)";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + $"… ({Invariant(text.Length)} chars)";

    /// <summary>A cell made safe for one Markdown table row: a pipe escaped, a line break shown as <c>\n</c>.</summary>
    public static string TableCell(string cell) => cell.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="grid"/> as a Markdown table, rows added while the text stays under <paramref name="maxChars"/>;
    /// <paramref name="shown"/> is how many made it.
    /// </summary>
    public static string Table(SqlGrid grid, int maxChars, out int shown)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var sb = new StringBuilder();
        sb.Append("| ").AppendJoin(" | ", grid.Columns.Select(TableCell)).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", grid.Columns.Count))).Append('\n');
        shown = 0;
        foreach (var row in grid.Rows)
        {
            string line = "| " + string.Join(" | ", row.Select(TableCell)) + " |\n";
            if (sb.Length + line.Length > maxChars)
            {
                break;
            }

            sb.Append(line);
            shown++;
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// <c>sql_query</c>'s answer: <c>N rows × M columns from connection/database (T ms)</c>, a sentence when the row
    /// cap or the text cap cut it, and the table.
    /// </summary>
    public static string Query(SqlRun run, int maxRows, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Grids.Count == 0)
        {
            return $"The query returned no result set ({run.Connection}/{run.Database}, {Millis(run.Elapsed)})";
        }

        var grid = run.Grids[0];
        string table = Table(grid, maxChars, out int shown);
        var header = new StringBuilder();
        header.Append(Count(grid.Rows.Count, "row")).Append(grid.More ? "+" : "")
            .Append(" × ").Append(Count(grid.Columns.Count, "column"))
            .Append(" from ").Append(run.Connection).Append('/').Append(run.Database)
            .Append(" (").Append(Millis(run.Elapsed)).Append(')');
        if (grid.More)
        {
            header.Append(" — the first ").Append(Invariant(maxRows)).Append(" shown, more exist (narrow it with WHERE or TOP, or raise max_rows)");
        }

        if (shown < grid.Rows.Count)
        {
            header.Append(" — ").Append(Invariant(shown)).Append(" fit the text cap; select fewer columns or rows for the rest");
        }

        return grid.Rows.Count == 0 ? header + "\n\n" + table : header + "\n\n" + table;
    }

    /// <summary>A catalog listing's answer: a header naming what and where, then the table (cut by the text cap, said so).</summary>
    public static string Listing(string singular, string plural, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = run.Grids.Count > 0 ? run.Grids[0] : new SqlGrid([], [], false);
        if (grid.Rows.Count == 0)
        {
            return $"No {plural} in {run.Connection}/{run.Database}";
        }

        string table = Table(grid, maxChars, out int shown);
        string header = $"{Count(grid.Rows.Count, singular, plural)}{(grid.More ? "+" : "")} in {run.Connection}/{run.Database}";
        if (grid.More || shown < grid.Rows.Count)
        {
            header += $" — the first {Invariant(shown)} shown; narrow it";
        }

        return header + "\n\n" + table;
    }

    /// <summary>
    /// <c>sql_describe</c>'s answer: the object's name and kind (its description on the next line), then its columns
    /// (the type written as a declaration would, <c>nvarchar(50)</c>, <c>decimal(19,4)</c>; a description column only
    /// when one has a description), then its keys out and in, its indexes (a UNIQUE constraint marked), its CHECK
    /// constraints and its triggers — each section only when it has a line. The result sets are read by position
    /// (<see cref="SqlCatalogQueries.Describe"/>); a shorter run, or a shorter row, reads as empty.
    /// </summary>
    public static string Describe(string schema, string name, string kind, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var columns = Set(run, 0);
        var sb = new StringBuilder();
        sb.Append(schema).Append('.').Append(name).Append(" (").Append(kind).Append(", ").Append(Count(columns.Rows.Count, "column"))
            .Append(") in ").Append(run.Connection).Append('/').Append(run.Database).Append('\n');
        if (Set(run, 3).Rows is [var described, ..] && At(described, 0) != Null)
        {
            sb.Append(OneLine(At(described, 0))).Append('\n');
        }

        // column, type, max_length, precision, scale, is_nullable, is_identity, is_computed, default, pk, description
        bool descriptions = columns.Rows.Any(c => At(c, 10) != Null);
        sb.Append(descriptions ? "\n| column | type | null | key | default | description |\n|---|---|---|---|---|---|\n" : "\n| column | type | null | key | default |\n|---|---|---|---|---|\n");
        int keyColumns = columns.Rows.Count(r => At(r, 9) != Null);
        foreach (var c in columns.Rows)
        {
            string type = TypeName(c[1], c[2], c[3], c[4]) + (At(c, 6) == "true" ? " identity" : "") + (At(c, 7) == "true" ? " computed" : "");
            string key = At(c, 9) == Null ? "" : "PK" + (keyColumns > 1 ? " " + c[9] : "");
            string def = At(c, 8) == Null ? "" : c[8];
            sb.Append("| ").Append(TableCell(c[0])).Append(" | ").Append(type).Append(" | ").Append(At(c, 5) == "true" ? "yes" : "no")
                .Append(" | ").Append(key).Append(" | ").Append(TableCell(def)).Append(" |");
            if (descriptions)
            {
                sb.Append(' ').Append(At(c, 10) == Null ? "" : TableCell(c[10])).Append(" |");
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
                // index, kind, is_unique, is_primary_key, keys, is_unique_constraint
                string flags = At(i, 3) == "true" ? "primary key, " : At(i, 5) == "true" ? "unique constraint, " : At(i, 2) == "true" ? "unique, " : "";
                sb.Append("- ").Append(At(i, 0) == Null ? "(heap)" : i[0]).Append(" (").Append(flags).Append(Kind(i[1])).Append("): ").Append(At(i, 4) == Null ? "" : i[4]).Append('\n');
            }
        }

        if (Set(run, 4).Rows.Count > 0)
        {
            sb.Append("\nCheck constraints:\n");
            foreach (var k in Set(run, 4).Rows)
            {
                // constraint, definition, is_disabled
                sb.Append("- ").Append(k[0]).Append(": ").Append(OneLine(At(k, 1))).Append(At(k, 2) == "true" ? " (disabled)" : "").Append('\n');
            }
        }

        if (Set(run, 5).Rows.Count > 0)
        {
            sb.Append("\nTriggers:\n");
            foreach (var t in Set(run, 5).Rows)
            {
                // trigger, is_instead_of_trigger, is_disabled, events
                string events = At(t, 3) == Null ? "" : " " + t[3];
                sb.Append("- ").Append(t[0]).Append(" (").Append(At(t, 1) == "true" ? "instead of" : "after").Append(events).Append(At(t, 2) == "true" ? ", disabled" : "").Append(")\n");
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary>
    /// <c>sql_columns</c>' answer (later on 2026-09-23): the <see cref="SqlCatalogQueries.Columns"/> rows as a listing,
    /// the type parts made one declaration (<see cref="TypeName"/>), nullability a word, the description column only
    /// when a column has one.
    /// </summary>
    public static string Columns(string pattern, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, kind, column, type, max_length, precision, scale, is_nullable, description
        var found = Set(run, 0);
        var rows = found.Rows.Select(r => new[] { r[0], r[1], r[2], TypeName(r[3], r[4], r[5], r[6]), At(r, 7) == "true" ? "yes" : "no", At(r, 8) }).ToList();
        var grid = WithoutEmptyColumn(new SqlGrid(["table", "kind", "column", "type", "null", "description"], rows, found.More), "description");
        string listing = Listing("column", "columns", run with { Grids = [grid] }, maxChars);
        return grid.Rows.Count == 0 ? listing + $" named like '{pattern}'" : listing;
    }

    /// <summary>
    /// <c>sql_indexes</c>' answer (later on 2026-09-23): a header naming the scope, one table row per index — kind and
    /// flags in one word run (<c>clustered PK</c>, <c>nonclustered unique</c>), key and included columns, the filter,
    /// the size — and, when <paramref name="usage"/> was read, its seeks, scans, lookups and updates since the server
    /// started, a nonclustered index nothing has read marked <see cref="UnusedMarker"/>. <paramref name="usageError"/>
    /// says why usage is missing; <paramref name="missing"/> (with <paramref name="missingError"/>) lists the server's
    /// missing-index suggestions after the table.
    /// </summary>
    public static string Indexes(string scope, SqlRun run, SqlGrid? usage, string? usageError, SqlGrid? missing, string? missingError, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        // table, index, kind, is_unique, is_primary_key, is_unique_constraint, is_disabled, keys, included, filter, fill_factor, size_kb, object_id, index_id
        var indexes = Set(run, 0);
        var used = new Dictionary<(string, string), string[]>();
        foreach (var u in usage?.Rows ?? [])
        {
            // object_id, index_id, user_seeks, user_scans, user_lookups, user_updates
            used[(u[0], u[1])] = u;
        }

        var sb = new StringBuilder();
        sb.Append(Count(indexes.Rows.Count, "index", "indexes")).Append(indexes.More ? "+" : "").Append(scope.Length > 0 ? " " + scope : "").Append(" in ").Append(run.Connection).Append('/').Append(run.Database);
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
                ? "\n| table | index | kind | keys | included | filter | size | seeks | scans | lookups | updates |\n|---|---|---|---|---|---|---|---|---|---|---|\n"
                : "\n| table | index | kind | keys | included | filter | size |\n|---|---|---|---|---|---|---|\n");
            int shown = 0;
            foreach (var i in indexes.Rows)
            {
                var line = new StringBuilder();
                line.Append("| ").Append(TableCell(i[0])).Append(" | ").Append(TableCell(At(i, 1) == Null ? "(heap)" : i[1])).Append(" | ").Append(IndexKind(i))
                    .Append(" | ").Append(TableCell(Blank(At(i, 7)))).Append(" | ").Append(TableCell(Blank(At(i, 8)))).Append(" | ").Append(TableCell(Blank(At(i, 9))))
                    .Append(" | ").Append(At(i, 11) == Null ? "" : Size(At(i, 11))).Append(" |");
                if (usage is not null)
                {
                    var u = used.TryGetValue((At(i, 12), At(i, 13)), out var row) ? row : ["", "", "0", "0", "0", "0"];
                    bool unread = u[2] == "0" && u[3] == "0" && u[4] == "0";
                    bool clustered = i[2].StartsWith("CLUSTERED", StringComparison.Ordinal) || At(i, 4) == "true";
                    line.Append(' ').Append(u[2]).Append(" | ").Append(u[3]).Append(" | ").Append(u[4]).Append(" | ").Append(u[5]).Append(unread && !clustered ? " " + UnusedMarker : "").Append(" |");
                }

                line.Append('\n');
                if (sb.Length + line.Length > maxChars)
                {
                    sb.Append($"[… the first {shown.ToString(CultureInfo.InvariantCulture)} shown; give a table or a schema to narrow it]\n");
                    break;
                }

                sb.Append(line);
                shown++;
            }
        }

        if (missing is not null || missingError is not null)
        {
            sb.Append("\nMissing-index suggestions (the optimizer's hints since the server started, not a design):\n");
            if (missingError is not null)
            {
                sb.Append(MissingUnavailable(missingError)).Append('\n');
            }
            else if (missing!.Rows.Count == 0)
            {
                sb.Append("- none\n");
            }
            else
            {
                foreach (var m in missing.Rows)
                {
                    // table, equality_columns, inequality_columns, included_columns, uses, impact
                    string keys = string.Join(", ", new[] { At(m, 1), At(m, 2) }.Where(k => k != Null));
                    sb.Append("- ").Append(m[0]).Append(" (").Append(keys).Append(')').Append(At(m, 3) == Null ? "" : " INCLUDE (" + m[3] + ")")
                        .Append(" — impact ").Append(At(m, 5)).Append("%, ").Append(At(m, 4)).Append(" uses\n");
                }
            }
        }

        string text = sb.ToString().TrimEnd('\n');
        return text.Length <= maxChars ? text : text[..maxChars] + "\n[… cut at the text cap]";
    }

    /// <summary>The mark on a nonclustered index no query has read since the server started. Pinned.</summary>
    public const string UnusedMarker = "(no reads since restart)";

    /// <summary>The line when the usage DMV refused the login. Pinned.</summary>
    public static string UsageUnavailable(string detail) => $"Usage not shown: the login may not read index usage (it needs VIEW SERVER STATE): {detail}";

    /// <summary>The line when the missing-index DMVs refused the login. Pinned.</summary>
    public static string MissingUnavailable(string detail) => $"- not available: the login may not read them (it needs VIEW SERVER STATE): {detail}";

    public const string NoPattern = "Error: give the column name (or part of it) to look for in \"pattern\"";

    /// <summary>The kind and flags of one <see cref="SqlCatalogQueries.Indexes"/> row as a short phrase: <c>clustered PK</c>, <c>nonclustered unique constraint</c>, <c>nonclustered, disabled</c>.</summary>
    private static string IndexKind(string[] i)
    {
        string kind = Kind(i[2]);
        string flag = At(i, 4) == "true" ? " PK" : At(i, 5) == "true" ? " unique constraint" : At(i, 3) == "true" ? " unique" : "";
        return kind + flag + (At(i, 6) == "true" ? ", disabled" : "");
    }

    private static string Kind(string typeDesc) => typeDesc.ToLowerInvariant().Replace('_', ' ');

    private static string Size(string kb) =>
        long.TryParse(kb, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n >= 10_240 ? (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : n.ToString(CultureInfo.InvariantCulture) + " KB"
            : kb;

    private static string Blank(string cell) => cell == Null ? "" : cell;

    private static string OneLine(string text) => text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>Result set <paramref name="index"/> of a run, or an empty one when the run has fewer.</summary>
    private static SqlGrid Set(SqlRun run, int index) => run.Grids.Count > index ? run.Grids[index] : new SqlGrid([], [], false);

    /// <summary>Cell <paramref name="index"/> of a row, or <see cref="Null"/> past its end.</summary>
    private static string At(string[] row, int index) => row.Length > index ? row[index] : Null;

    /// <summary>
    /// <paramref name="grid"/> less the column named <paramref name="name"/> when every row's cell in it is NULL, and
    /// with its NULL cells blank when some are not (later on 2026-09-23: the description column of an undocumented
    /// database would be a wall of NULL, and an undocumented column among documented ones has nothing to say).
    /// </summary>
    public static SqlGrid WithoutEmptyColumn(SqlGrid grid, string name)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int column = grid.Columns.ToList().IndexOf(name);
        if (column < 0)
        {
            return grid;
        }

        if (grid.Rows.Any(r => At(r, column) != Null))
        {
            return new SqlGrid(grid.Columns, grid.Rows.Select(r => r.Select((cell, i) => i == column && cell == Null ? "" : cell).ToArray()).ToList(), grid.More);
        }

        return new SqlGrid(
            grid.Columns.Where((_, i) => i != column).ToList(),
            grid.Rows.Select(r => r.Where((_, i) => i != column).ToArray()).ToList(),
            grid.More);
    }

    /// <summary><c>sql_relationships</c>' answer: one line per column pair, <c>from.col -> to.col (constraint)</c>.</summary>
    public static string Relationships(string? table, SqlRun run, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(run);
        var grid = run.Grids.Count > 0 ? run.Grids[0] : new SqlGrid([], [], false);
        string where = $"{run.Connection}/{run.Database}";
        string scope = string.IsNullOrWhiteSpace(table) ? "" : $" touching {table}";
        if (grid.Rows.Count == 0)
        {
            return $"No foreign keys{scope} in {where}";
        }

        var sb = new StringBuilder();
        sb.Append(Count(grid.Rows.Count, "foreign-key column pair")).Append(grid.More ? "+" : "").Append(scope).Append(" in ").Append(where).Append(" (join on these):");
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
            sb.Append($"\n[… the first {Invariant(shown)} shown; give a table to narrow it]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// A column's type as a declaration writes it, from <c>sys.columns</c>' parts: <c>nvarchar(50)</c> (the byte
    /// length halved), <c>varchar(max)</c>, <c>decimal(19,4)</c>, <c>datetime2(7)</c>; the rest bare.
    /// </summary>
    public static string TypeName(string type, string maxLength, string precision, string scale)
    {
        ArgumentNullException.ThrowIfNull(type);
        int length = int.TryParse(maxLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : 0;
        string Length(int divisor) => length == -1 ? "max" : Invariant(length / divisor);
        return type.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({Length(1)})",
            "nvarchar" or "nchar" => $"{type}({Length(2)})",
            "decimal" or "numeric" => $"{type}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
            _ => type,
        };
    }

    // ─── shared ─────────────────────────────────────────────────────────────────

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    public static string Count(int n, string singular, string? plural = null) =>
        Invariant(n) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static string Millis(TimeSpan elapsed) => Invariant((long)Math.Round(elapsed.TotalMilliseconds)) + " ms";

    private static string Invariant(long n) => n.ToString(CultureInfo.InvariantCulture);
}
