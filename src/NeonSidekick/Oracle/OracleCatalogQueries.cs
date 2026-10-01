namespace NeonSidekick.Oracle;

/// <summary>
/// The app's own dictionary statements (2026-09-30), <see cref="Sql.SqlCatalogQueries"/>' part for Oracle: run through
/// <see cref="OracleAccess.RunAsync"/> like a model's query — the same read-only session and transaction — but never through
/// <see cref="OracleReadOnlyGate"/>. Every value a model sends is bound (<c>:name</c>), never spliced in; a name is bound as
/// the dictionary spells it (<see cref="OracleIdentifier.Normalize"/>, the tool's job). The <c>ALL_*</c> views, so each
/// answers with what the account may see; every one written <c>SYS.</c>, since a call's <c>CURRENT_SCHEMA</c> is searched
/// before the public synonyms and a table there named <c>ALL_OBJECTS</c> would answer instead. Oracle's own schemas
/// (<c>ORACLE_MAINTAINED</c>) are left out of the listings; 12c and later. A description is the <c>COMMENT ON</c> text.
/// ODP.NET runs one statement per command, so a many-part answer is a list of statements.
/// </summary>
public static class OracleCatalogQueries
{
    /// <summary>The schemas (users) the account can see, Oracle's own left out, with their table and view counts; <c>your own</c> marks the account's.</summary>
    public const string Schemas =
        """
        SELECT u.username AS "schema",
               (SELECT COUNT(*) FROM sys.all_tables t WHERE t.owner = u.username) AS "tables",
               (SELECT COUNT(*) FROM sys.all_views v WHERE v.owner = u.username) AS "views",
               TO_CHAR(u.created, 'YYYY-MM-DD') AS "created",
               CASE WHEN u.username = SYS_CONTEXT('USERENV', 'SESSION_USER') THEN 'your own' END AS "note"
        FROM sys.all_users u
        WHERE u.oracle_maintained = 'N'
        ORDER BY u.username
        """;

    /// <summary>
    /// The tables and views, <c>:schema</c> (null = all) and <c>:pattern</c> (a LIKE pattern over the name or
    /// <c>SCHEMA.NAME</c>, case-insensitive, null = all) narrowing it; a table's rows are the optimizer's count (as of its
    /// last statistics, free); the last column its comment.
    /// </summary>
    public const string Tables =
        """
        SELECT o.owner AS "schema", o.object_name AS "name", LOWER(o.object_type) AS "type", t.num_rows AS "rows", c.comments AS "description"
        FROM sys.all_objects o
        JOIN sys.all_users u ON u.username = o.owner AND u.oracle_maintained = 'N'
        LEFT JOIN sys.all_tables t ON o.object_type = 'TABLE' AND t.owner = o.owner AND t.table_name = o.object_name
        LEFT JOIN sys.all_tab_comments c ON c.owner = o.owner AND c.table_name = o.object_name
        WHERE o.object_type IN ('TABLE', 'VIEW') AND o.secondary = 'N' AND o.object_name NOT LIKE 'BIN$%'
          AND (:schema IS NULL OR o.owner = :schema)
          AND (:pattern IS NULL OR UPPER(o.object_name) LIKE UPPER(:pattern) OR UPPER(o.owner || '.' || o.object_name) LIKE UPPER(:pattern))
        ORDER BY o.owner, o.object_name
        """;

    /// <summary>
    /// The tables and views <c>:owner</c>.<c>:name</c> may mean: that one, or — with no owner — every schema's of that name,
    /// <c>exact</c> marking the one in the call's schema (<c>CURRENT_SCHEMA</c>), so <c>EMPLOYEES</c> finds <c>HR.EMPLOYEES</c>.
    /// </summary>
    public const string Resolve =
        """
        SELECT o.owner AS "schema", o.object_name AS "name", LOWER(o.object_type) AS "type",
               CASE WHEN o.owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') THEN 1 ELSE 0 END AS "exact"
        FROM sys.all_objects o
        WHERE o.object_type IN ('TABLE', 'VIEW') AND o.object_name = :name AND (:owner IS NULL OR o.owner = :owner)
        ORDER BY 4 DESC, o.owner
        """;

    /// <summary>
    /// Six statements on the object <c>:owner</c>.<c>:name</c>, read by position in <see cref="OracleText.Describe"/>:
    /// 0 its columns (type parts, nullability, identity, virtual, default, primary-key position, comment); 1 the foreign keys
    /// out of and into it; 2 its indexes with their key columns (a key's index marked P or U); 3 its own comment; 4 its CHECK
    /// constraints; 5 its triggers.
    /// </summary>
    public static readonly IReadOnlyList<string> Describe =
    [
        """
        SELECT c.column_name AS "column", c.data_type AS "type", c.data_length, c.data_precision, c.data_scale, c.char_length, c.char_used,
               c.nullable, c.identity_column, c.virtual_column, c.data_default AS "default",
               (SELECT cc.position FROM sys.all_constraints k
                JOIN sys.all_cons_columns cc ON cc.owner = k.owner AND cc.constraint_name = k.constraint_name
                WHERE k.owner = c.owner AND k.table_name = c.table_name AND k.constraint_type = 'P' AND cc.column_name = c.column_name) AS "pk",
               m.comments AS "description"
        FROM sys.all_tab_cols c
        LEFT JOIN sys.all_col_comments m ON m.owner = c.owner AND m.table_name = c.table_name AND m.column_name = c.column_name
        WHERE c.owner = :owner AND c.table_name = :name AND c.hidden_column = 'NO'
        ORDER BY c.column_id
        """,
        """
        SELECT CASE WHEN k.owner = :owner AND k.table_name = :name THEN 'out' ELSE 'in' END AS "direction", k.constraint_name AS "constraint",
               k.owner || '.' || k.table_name AS "from_table", fc.column_name AS "from_column",
               r.owner || '.' || r.table_name AS "to_table", rc.column_name AS "to_column"
        FROM sys.all_constraints k
        JOIN sys.all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
        JOIN sys.all_cons_columns fc ON fc.owner = k.owner AND fc.constraint_name = k.constraint_name
        JOIN sys.all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = fc.position
        WHERE k.constraint_type = 'R' AND ((k.owner = :owner AND k.table_name = :name) OR (r.owner = :owner AND r.table_name = :name))
        ORDER BY 1 DESC, k.constraint_name, fc.position
        """,
        """
        SELECT i.index_name AS "index", i.index_type AS "kind", i.uniqueness,
               (SELECT MIN(k.constraint_type) FROM sys.all_constraints k
                WHERE k.owner = i.table_owner AND k.table_name = i.table_name AND k.index_name = i.index_name AND k.constraint_type IN ('P', 'U')) AS "constraint",
               (SELECT LISTAGG(ic.column_name || CASE WHEN ic.descend = 'DESC' THEN ' DESC' END, ', ') WITHIN GROUP (ORDER BY ic.column_position)
                FROM sys.all_ind_columns ic WHERE ic.index_owner = i.owner AND ic.index_name = i.index_name) AS "keys",
               i.status
        FROM sys.all_indexes i
        WHERE i.table_owner = :owner AND i.table_name = :name AND i.index_type <> 'LOB'
        ORDER BY i.index_name
        """,
        """
        SELECT c.comments AS "description"
        FROM sys.all_tab_comments c
        WHERE c.owner = :owner AND c.table_name = :name
        """,
        """
        SELECT k.constraint_name AS "constraint", k.search_condition AS "definition", k.status, k.generated
        FROM sys.all_constraints k
        WHERE k.owner = :owner AND k.table_name = :name AND k.constraint_type = 'C'
        ORDER BY k.constraint_name
        """,
        """
        SELECT t.trigger_name AS "trigger", t.trigger_type, t.triggering_event, t.status
        FROM sys.all_triggers t
        WHERE t.table_owner = :owner AND t.table_name = :name
        ORDER BY t.trigger_name
        """,
    ];

    /// <summary>
    /// The foreign-key join paths, one row per column pair: every one outside Oracle's schemas, or those touching
    /// <c>:owner</c>.<c>:name</c>, or those touching schema <c>:schema</c>.
    /// </summary>
    public const string Relationships =
        """
        SELECT k.constraint_name AS "constraint", k.owner || '.' || k.table_name AS "from_table", fc.column_name AS "from_column",
               r.owner || '.' || r.table_name AS "to_table", rc.column_name AS "to_column"
        FROM sys.all_constraints k
        JOIN sys.all_constraints r ON r.owner = k.r_owner AND r.constraint_name = k.r_constraint_name
        JOIN sys.all_cons_columns fc ON fc.owner = k.owner AND fc.constraint_name = k.constraint_name
        JOIN sys.all_cons_columns rc ON rc.owner = r.owner AND rc.constraint_name = r.constraint_name AND rc.position = fc.position
        JOIN sys.all_users u ON u.username = k.owner AND u.oracle_maintained = 'N'
        WHERE k.constraint_type = 'R'
          AND (:name IS NULL OR (k.owner = :owner AND k.table_name = :name) OR (r.owner = :owner AND r.table_name = :name))
          AND (:schema IS NULL OR k.owner = :schema OR r.owner = :schema)
        ORDER BY 2, 1, fc.position
        """;

    /// <summary>
    /// The columns whose name is <c>LIKE :pattern</c> (case-insensitive) across every table and view, <c>:schema</c> narrowing
    /// it: where a column lives, its type parts, nullability, comment.
    /// </summary>
    public const string Columns =
        """
        SELECT c.owner || '.' || c.table_name AS "table", LOWER(o.object_type) AS "kind", c.column_name AS "column", c.data_type AS "type",
               c.data_length, c.data_precision, c.data_scale, c.char_length, c.char_used, c.nullable, m.comments AS "description"
        FROM sys.all_tab_columns c
        JOIN sys.all_objects o ON o.owner = c.owner AND o.object_name = c.table_name AND o.object_type IN ('TABLE', 'VIEW')
        JOIN sys.all_users u ON u.username = c.owner AND u.oracle_maintained = 'N'
        LEFT JOIN sys.all_col_comments m ON m.owner = c.owner AND m.table_name = c.table_name AND m.column_name = c.column_name
        WHERE UPPER(c.column_name) LIKE UPPER(:pattern) AND (:schema IS NULL OR c.owner = :schema) AND c.table_name NOT LIKE 'BIN$%'
        ORDER BY 1, c.column_id
        """;

    /// <summary>
    /// The indexes of every table outside Oracle's schemas, or of <c>:owner</c>.<c>:name</c>, or of schema <c>:schema</c>: kind,
    /// uniqueness, the key constraint an index backs (P or U), key columns, status and visibility, the optimizer's counts and
    /// when it last analyzed them — views any reader may see. The last column (the index's owner) is what
    /// <see cref="IndexUsage"/> joins on.
    /// </summary>
    public const string Indexes =
        """
        SELECT i.table_owner || '.' || i.table_name AS "table", i.index_name AS "index", i.index_type AS "kind", i.uniqueness,
               (SELECT MIN(k.constraint_type) FROM sys.all_constraints k
                WHERE k.owner = i.table_owner AND k.table_name = i.table_name AND k.index_name = i.index_name AND k.constraint_type IN ('P', 'U')) AS "constraint",
               (SELECT LISTAGG(ic.column_name || CASE WHEN ic.descend = 'DESC' THEN ' DESC' END, ', ') WITHIN GROUP (ORDER BY ic.column_position)
                FROM sys.all_ind_columns ic WHERE ic.index_owner = i.owner AND ic.index_name = i.index_name) AS "keys",
               i.status, i.visibility, i.num_rows, i.distinct_keys, TO_CHAR(i.last_analyzed, 'YYYY-MM-DD') AS "last_analyzed", i.owner
        FROM sys.all_indexes i
        JOIN sys.all_users u ON u.username = i.table_owner AND u.oracle_maintained = 'N'
        WHERE i.index_type <> 'LOB'
          AND (:name IS NULL OR (i.table_owner = :owner AND i.table_name = :name))
          AND (:schema IS NULL OR i.table_owner = :schema)
        ORDER BY 1, 2
        """;

    /// <summary>
    /// How the indexes have been used as <c>DBA_INDEX_USAGE</c> (12.2 and later) tracked it — sampled by default, so a count is
    /// a floor. A <c>DBA_</c> view: the account needs <c>SELECT_CATALOG_ROLE</c> or <c>SELECT ANY DICTIONARY</c>; without it the
    /// tool shows the indexes and says why the usage is missing.
    /// </summary>
    public const string IndexUsage =
        """
        SELECT u.owner, u.name, u.total_access_count, u.total_exec_count, u.total_rows_returned, TO_CHAR(u.last_used, 'YYYY-MM-DD HH24:MI') AS "last_used"
        FROM sys.dba_index_usage u
        WHERE (:owner IS NULL OR u.owner = :owner)
        """;

    /// <summary>The wizard's and the check's first question: who the connection signed in as, in which container, and the server's version (a public view).</summary>
    public const string WhoAmI =
        """
        SELECT USER AS "user", SYS_CONTEXT('USERENV', 'CON_NAME') AS "container",
               (SELECT MAX(p.version) FROM sys.product_component_version p) AS "version"
        FROM sys.dual
        """;

    /// <summary>
    /// What the account could change, were the tools to let it (the wizard's warning; the tools never do): system privileges
    /// that create, alter or reach <c>ANY</c> object (reading ones left out), the table privileges it holds itself or through
    /// a role that write, and the tables it owns.
    /// </summary>
    public const string WritePowers =
        """
        SELECT p.privilege AS "what" FROM sys.session_privs p
        WHERE (p.privilege LIKE 'CREATE %' AND p.privilege <> 'CREATE SESSION')
           OR (p.privilege LIKE '% ANY %' AND p.privilege NOT LIKE 'SELECT ANY %' AND p.privilege NOT LIKE 'READ ANY %' AND p.privilege NOT LIKE 'DEBUG ANY %')
           OR p.privilege IN ('ALTER DATABASE', 'ALTER SYSTEM', 'BECOME USER')
        UNION ALL
        SELECT t.privilege || ' ON ' || t.owner || '.' || t.table_name FROM sys.user_tab_privs_recd t
        WHERE t.privilege IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'INDEX')
        UNION ALL
        SELECT r.privilege || ' ON ' || r.owner || '.' || r.table_name FROM sys.role_tab_privs r
        WHERE r.privilege IN ('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'INDEX')
        UNION ALL
        SELECT 'owns ' || COUNT(*) || ' tables' FROM sys.user_tables HAVING COUNT(*) > 0
        """;
}
