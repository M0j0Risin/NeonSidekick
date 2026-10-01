namespace NeonSidekick.MySql;

/// <summary>
/// The app's own catalog statements (2026-09-30), <see cref="Oracle.OracleCatalogQueries"/>' part for MySQL and MariaDB: run
/// through <see cref="MySqlAccess.RunAsync"/> like a model's query — the same read-only session and transaction — but never
/// through <see cref="MySqlReadOnlyGate"/>. Every value a model sends is bound (<c>@name</c>), never spliced in. Only
/// <c>information_schema</c> (each answers with what the account may see) and columns MySQL 8.0.16+ and MariaDB 10.2+ both
/// carry. The server's own databases are left out of the listings. A listing with no <c>@database</c> covers the call's
/// database (<c>DATABASE()</c>, the connection's), or every database when none is in force. A description is the
/// <c>COMMENT</c>. One statement per command, so a many-part answer is a list.
/// </summary>
public static class MySqlCatalogQueries
{
    /// <summary>The server's own databases, left out of every listing.</summary>
    public const string SystemDatabases = "'mysql', 'information_schema', 'performance_schema', 'sys'";

    /// <summary>The database a listing covers: the call's, else the connection's, else null (every one).</summary>
    private const string Scope = "COALESCE(@database, DATABASE())";

    /// <summary>The databases the account can see, the server's own left out, with their table and view counts and character set.</summary>
    public const string Databases =
        $$"""
        SELECT s.SCHEMA_NAME AS `database`,
               (SELECT COUNT(*) FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = s.SCHEMA_NAME AND t.TABLE_TYPE = 'BASE TABLE') AS `tables`,
               (SELECT COUNT(*) FROM information_schema.TABLES t WHERE t.TABLE_SCHEMA = s.SCHEMA_NAME AND t.TABLE_TYPE = 'VIEW') AS `views`,
               s.DEFAULT_CHARACTER_SET_NAME AS `charset`, s.DEFAULT_COLLATION_NAME AS `collation`,
               CASE WHEN s.SCHEMA_NAME = DATABASE() THEN 'in force' END AS `note`
        FROM information_schema.SCHEMATA s
        WHERE s.SCHEMA_NAME NOT IN ({{SystemDatabases}})
        ORDER BY s.SCHEMA_NAME
        """;

    /// <summary>The tables and views, <c>@pattern</c> (a LIKE pattern over the name or <c>database.name</c>, null = all) narrowing them; a table's rows are the engine's estimate.</summary>
    public const string Tables =
        $$"""
        SELECT t.TABLE_SCHEMA AS `database`, t.TABLE_NAME AS `name`, CASE t.TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END AS `type`,
               t.TABLE_ROWS AS `rows`, CASE WHEN t.TABLE_TYPE = 'VIEW' THEN NULL ELSE NULLIF(t.TABLE_COMMENT, '') END AS `description`
        FROM information_schema.TABLES t
        WHERE t.TABLE_SCHEMA NOT IN ({{SystemDatabases}})
          AND ({{Scope}} IS NULL OR t.TABLE_SCHEMA = {{Scope}})
          AND (@pattern IS NULL OR LOWER(t.TABLE_NAME) LIKE LOWER(@pattern) OR LOWER(CONCAT(t.TABLE_SCHEMA, '.', t.TABLE_NAME)) LIKE LOWER(@pattern))
        ORDER BY 1, 2
        """;

    /// <summary>The tables and views <c>@db</c>.<c>@name</c> may mean (case-insensitive): that one, or — with no database — every database's of that name, <c>exact</c> marking the one in force.</summary>
    public const string Resolve =
        $$"""
        SELECT t.TABLE_SCHEMA AS `database`, t.TABLE_NAME AS `name`, CASE t.TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END AS `type`,
               CASE WHEN t.TABLE_SCHEMA = DATABASE() THEN 1 ELSE 0 END AS `exact`
        FROM information_schema.TABLES t
        WHERE t.TABLE_SCHEMA NOT IN ({{SystemDatabases}}) AND LOWER(t.TABLE_NAME) = LOWER(@name) AND (@db IS NULL OR LOWER(t.TABLE_SCHEMA) = LOWER(@db))
        ORDER BY 4 DESC, 1
        """;

    /// <summary>
    /// Six statements on the object <c>@db</c>.<c>@name</c>, read by position in <see cref="MySqlText.Describe"/>: 0 its columns
    /// (the type as declared, nullability, extra, default, primary-key position, comment); 1 the foreign keys out of and into it;
    /// 2 its indexes with their key columns; 3 its own comment; 4 its CHECK constraints; 5 its triggers.
    /// </summary>
    public static readonly IReadOnlyList<string> Describe =
    [
        """
        SELECT c.COLUMN_NAME AS `column`, c.COLUMN_TYPE AS `type`, c.IS_NULLABLE AS `nullable`, c.EXTRA AS `extra`, c.COLUMN_DEFAULT AS `default`,
               (SELECT k.ORDINAL_POSITION FROM information_schema.KEY_COLUMN_USAGE k
                WHERE k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME AND k.COLUMN_NAME = c.COLUMN_NAME AND k.CONSTRAINT_NAME = 'PRIMARY') AS `pk`,
               NULLIF(c.COLUMN_COMMENT, '') AS `description`
        FROM information_schema.COLUMNS c
        WHERE c.TABLE_SCHEMA = @db AND c.TABLE_NAME = @name
        ORDER BY c.ORDINAL_POSITION
        """,
        """
        SELECT CASE WHEN k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @name THEN 'out' ELSE 'in' END AS `direction`, k.CONSTRAINT_NAME AS `constraint`,
               CONCAT(k.TABLE_SCHEMA, '.', k.TABLE_NAME) AS `from_table`, k.COLUMN_NAME AS `from_column`,
               CONCAT(k.REFERENCED_TABLE_SCHEMA, '.', k.REFERENCED_TABLE_NAME) AS `to_table`, k.REFERENCED_COLUMN_NAME AS `to_column`
        FROM information_schema.KEY_COLUMN_USAGE k
        WHERE k.REFERENCED_TABLE_NAME IS NOT NULL
          AND ((k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @name) OR (k.REFERENCED_TABLE_SCHEMA = @db AND k.REFERENCED_TABLE_NAME = @name))
        ORDER BY 1 DESC, 2, k.ORDINAL_POSITION
        """,
        """
        SELECT s.INDEX_NAME AS `index`, MAX(s.INDEX_TYPE) AS `kind`, MIN(s.NON_UNIQUE) AS `non_unique`,
               GROUP_CONCAT(CONCAT(s.COLUMN_NAME, IFNULL(CONCAT('(', s.SUB_PART, ')'), ''), CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END)
                            ORDER BY s.SEQ_IN_INDEX SEPARATOR ', ') AS `keys`
        FROM information_schema.STATISTICS s
        WHERE s.TABLE_SCHEMA = @db AND s.TABLE_NAME = @name
        GROUP BY s.INDEX_NAME
        ORDER BY s.INDEX_NAME <> 'PRIMARY', s.INDEX_NAME
        """,
        """
        SELECT NULLIF(t.TABLE_COMMENT, '') AS `description`
        FROM information_schema.TABLES t
        WHERE t.TABLE_SCHEMA = @db AND t.TABLE_NAME = @name AND t.TABLE_TYPE <> 'VIEW'
        """,
        """
        SELECT cc.CONSTRAINT_NAME AS `constraint`, cc.CHECK_CLAUSE AS `definition`
        FROM information_schema.CHECK_CONSTRAINTS cc
        JOIN information_schema.TABLE_CONSTRAINTS tc
          ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME AND tc.CONSTRAINT_TYPE = 'CHECK'
        WHERE tc.TABLE_SCHEMA = @db AND tc.TABLE_NAME = @name
        ORDER BY 1
        """,
        """
        SELECT g.TRIGGER_NAME AS `trigger`, g.ACTION_TIMING AS `timing`, g.EVENT_MANIPULATION AS `event`
        FROM information_schema.TRIGGERS g
        WHERE g.EVENT_OBJECT_SCHEMA = @db AND g.EVENT_OBJECT_TABLE = @name
        ORDER BY 1
        """,
    ];

    /// <summary>The foreign-key join paths, one row per column pair: every one in scope, or those touching <c>@db</c>.<c>@name</c>.</summary>
    public const string Relationships =
        $$"""
        SELECT k.CONSTRAINT_NAME AS `constraint`, CONCAT(k.TABLE_SCHEMA, '.', k.TABLE_NAME) AS `from_table`, k.COLUMN_NAME AS `from_column`,
               CONCAT(k.REFERENCED_TABLE_SCHEMA, '.', k.REFERENCED_TABLE_NAME) AS `to_table`, k.REFERENCED_COLUMN_NAME AS `to_column`
        FROM information_schema.KEY_COLUMN_USAGE k
        WHERE k.REFERENCED_TABLE_NAME IS NOT NULL AND k.TABLE_SCHEMA NOT IN ({{SystemDatabases}})
          AND (@name IS NULL OR (k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @name) OR (k.REFERENCED_TABLE_SCHEMA = @db AND k.REFERENCED_TABLE_NAME = @name))
          AND (@name IS NOT NULL OR {{Scope}} IS NULL OR k.TABLE_SCHEMA = {{Scope}} OR k.REFERENCED_TABLE_SCHEMA = {{Scope}})
        ORDER BY 2, 1, k.ORDINAL_POSITION
        """;

    /// <summary>The columns whose name is <c>LIKE @pattern</c> (case-insensitive) across the tables and views in scope: where each lives, its type, nullability, comment.</summary>
    public const string Columns =
        $$"""
        SELECT CONCAT(c.TABLE_SCHEMA, '.', c.TABLE_NAME) AS `table`, CASE t.TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END AS `kind`,
               c.COLUMN_NAME AS `column`, c.COLUMN_TYPE AS `type`, c.IS_NULLABLE AS `nullable`, NULLIF(c.COLUMN_COMMENT, '') AS `description`
        FROM information_schema.COLUMNS c
        JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
        WHERE c.TABLE_SCHEMA NOT IN ({{SystemDatabases}}) AND LOWER(c.COLUMN_NAME) LIKE LOWER(@pattern)
          AND ({{Scope}} IS NULL OR c.TABLE_SCHEMA = {{Scope}})
        ORDER BY 1, c.ORDINAL_POSITION
        """;

    /// <summary>
    /// The indexes of the tables in scope, or of <c>@db</c>.<c>@name</c>: kind, uniqueness, key columns, the cardinality estimate.
    /// The last two columns are what <see cref="IndexUsage"/> joins on.
    /// </summary>
    public const string Indexes =
        $$"""
        SELECT CONCAT(s.TABLE_SCHEMA, '.', s.TABLE_NAME) AS `table`, s.INDEX_NAME AS `index`, MAX(s.INDEX_TYPE) AS `kind`, MIN(s.NON_UNIQUE) AS `non_unique`,
               GROUP_CONCAT(CONCAT(s.COLUMN_NAME, IFNULL(CONCAT('(', s.SUB_PART, ')'), ''), CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END)
                            ORDER BY s.SEQ_IN_INDEX SEPARATOR ', ') AS `keys`,
               MAX(s.CARDINALITY) AS `cardinality`, s.TABLE_SCHEMA AS `database`, s.TABLE_NAME AS `name`
        FROM information_schema.STATISTICS s
        WHERE s.TABLE_SCHEMA NOT IN ({{SystemDatabases}})
          AND (@name IS NULL OR (s.TABLE_SCHEMA = @db AND s.TABLE_NAME = @name))
          AND (@name IS NOT NULL OR {{Scope}} IS NULL OR s.TABLE_SCHEMA = {{Scope}})
        GROUP BY s.TABLE_SCHEMA, s.TABLE_NAME, s.INDEX_NAME
        ORDER BY 1, s.INDEX_NAME <> 'PRIMARY', 2
        """;

    /// <summary>
    /// How the indexes have been read and written since the server started (<c>performance_schema</c>): MariaDB ships it off and
    /// an account may not read it — then the tool shows the indexes and says why the usage is missing.
    /// </summary>
    public const string IndexUsage =
        $$"""
        SELECT u.OBJECT_SCHEMA AS `database`, u.OBJECT_NAME AS `table`, u.INDEX_NAME AS `index`, u.COUNT_READ AS `reads`, u.COUNT_WRITE AS `writes`
        FROM performance_schema.table_io_waits_summary_by_index_usage u
        WHERE u.INDEX_NAME IS NOT NULL AND u.OBJECT_SCHEMA NOT IN ({{SystemDatabases}})
          AND (@name IS NULL OR (u.OBJECT_SCHEMA = @db AND u.OBJECT_NAME = @name))
        """;

    /// <summary>The wizard's and the check's first question: who the connection signed in as, the database in force, the server's version.</summary>
    public const string WhoAmI = "SELECT CURRENT_USER() AS `user`, DATABASE() AS `database`, VERSION() AS `version`";

    /// <summary>The account's grants, for the wizard's write warning (<see cref="MySqlText.WritePowers"/>).</summary>
    public const string Grants = "SHOW GRANTS";
}
