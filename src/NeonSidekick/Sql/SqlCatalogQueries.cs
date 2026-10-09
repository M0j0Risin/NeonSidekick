namespace NeonSidekick.Sql;

/// <summary>
/// The app's own catalog batches (2026-09-23), run through <see cref="SqlAccess.RunAsync"/> like a model's query
/// but never through <see cref="SqlReadOnlyGate"/> (some declare a variable first). Every value a model sends
/// is bound as a parameter, never spliced in. <c>sys.*</c> rather than <c>INFORMATION_SCHEMA</c>: the
/// <c>mcp-mssql-read</c> note found <c>REFERENTIAL_CONSTRAINTS</c> missing on its SQL Server 2022 target, and
/// <c>sys.*</c> carries the schema that server's <c>list_tables</c> dropped. <c>FOR XML PATH</c> joins lists
/// rather than <c>STRING_AGG</c>, so a server before 2017 answers too. A description is the
/// <c>MS_Description</c> extended property (later on 2026-09-23, the user's pick): how SSMS, AdventureWorks and
/// most documented databases write down what a table or column means.
/// </summary>
public static class SqlCatalogQueries
{
    /// <summary>
    /// The other databases the login may open, the server's own four (master, tempdb, model, msdb) left out: what the connection's lock
    /// keeps the tools out of but the login could reach, for the wizard's test and <c>--sql-check</c> (2026-10-09; <c>sql_databases</c>'
    /// listing until the lock). Empty when the server holds the login to its database too.
    /// </summary>
    public const string OtherDatabases =
        """
        SELECT d.name AS [database]
        FROM sys.databases AS d
        WHERE d.database_id > 4 AND d.name <> DB_NAME() AND HAS_DBACCESS(d.name) = 1
        ORDER BY d.name
        """;

    /// <summary>
    /// The user tables and views, <c>@schema</c> (null = all) and <c>@pattern</c> (a LIKE pattern over the name or
    /// <c>schema.name</c>, null = all) narrowing it; a table's rows are the partition counts (approximate, free);
    /// the last column its description (<see cref="SqlText.WithoutEmptyColumn"/> drops it when none has one).
    /// </summary>
    public const string Tables =
        """
        SELECT s.name AS [schema], o.name AS [name], CASE o.type WHEN 'U' THEN 'table' ELSE 'view' END AS [type],
               (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS [rows],
               CAST(ep.value AS nvarchar(400)) AS [description]
        FROM sys.objects AS o
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
          AND (@schema IS NULL OR s.name = @schema)
          AND (@pattern IS NULL OR o.name LIKE @pattern OR s.name + '.' + o.name LIKE @pattern)
        ORDER BY s.name, o.name
        """;

    /// <summary>
    /// The tables and views <c>@name</c> may mean: the one <c>OBJECT_ID</c> resolves (a <c>schema.name</c>, brackets
    /// allowed, or a bare name in the login's default schema), plus — for a bare name — every schema's of that
    /// name, so <c>Person</c> finds <c>Person.Person</c>. <c>[exact]</c> marks the one <c>OBJECT_ID</c> resolved.
    /// </summary>
    public const string Resolve =
        """
        SELECT o.object_id AS [id], s.name AS [schema], o.name AS [name], CASE o.type WHEN 'U' THEN 'table' ELSE 'view' END AS [type],
               CASE WHEN o.object_id = OBJECT_ID(@name) THEN 1 ELSE 0 END AS [exact]
        FROM sys.objects AS o
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.type IN ('U', 'V')
          AND (o.object_id = OBJECT_ID(@name) OR (PARSENAME(@name, 2) IS NULL AND o.name = PARSENAME(@name, 1)))
        ORDER BY [exact] DESC, s.name
        """;

    /// <summary>
    /// Six result sets on the object <c>@id</c>, read by position in <see cref="SqlText.Describe"/>:
    /// 0 its columns (type parts, nullability, identity, computed, default, primary-key position, description);
    /// 1 the foreign keys out of and into it; 2 its indexes with their key columns (a UNIQUE constraint is one,
    /// marked); 3 its own description; 4 its CHECK constraints; 5 its triggers with the events they fire on.
    /// </summary>
    public const string Describe =
        """
        SELECT c.name AS [column], TYPE_NAME(c.user_type_id) AS [type], c.max_length, c.precision, c.scale,
               c.is_nullable, c.is_identity, c.is_computed,
               OBJECT_DEFINITION(c.default_object_id) AS [default],
               (SELECT ic.key_ordinal FROM sys.indexes AS i JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) AS [pk],
               CAST(ep.value AS nvarchar(400)) AS [description]
        FROM sys.columns AS c
        LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
        WHERE c.object_id = @id
        ORDER BY c.column_id;

        SELECT CASE WHEN fk.parent_object_id = @id THEN 'out' ELSE 'in' END AS [direction], fk.name AS [constraint],
               SCHEMA_NAME(p.schema_id) + '.' + p.name AS [from_table], pc.name AS [from_column],
               SCHEMA_NAME(r.schema_id) + '.' + r.name AS [to_table], rc.name AS [to_column]
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.objects AS p ON p.object_id = fkc.parent_object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.objects AS r ON r.object_id = fkc.referenced_object_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = @id OR fk.referenced_object_id = @id
        ORDER BY [direction] DESC, fk.name, fkc.constraint_column_id;

        SELECT i.name AS [index], i.type_desc AS [kind], i.is_unique, i.is_primary_key,
               STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                      FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                      ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [keys],
               i.is_unique_constraint
        FROM sys.indexes AS i
        WHERE i.object_id = @id AND i.type > 0
        ORDER BY i.index_id;

        SELECT CAST(ep.value AS nvarchar(1000)) AS [description]
        FROM sys.extended_properties AS ep
        WHERE ep.class = 1 AND ep.major_id = @id AND ep.minor_id = 0 AND ep.name = 'MS_Description';

        SELECT cc.name AS [constraint], cc.definition, cc.is_disabled
        FROM sys.check_constraints AS cc
        WHERE cc.parent_object_id = @id
        ORDER BY cc.name;

        SELECT t.name AS [trigger], t.is_instead_of_trigger, t.is_disabled,
               STUFF((SELECT ', ' + te.type_desc FROM sys.trigger_events AS te WHERE te.object_id = t.object_id
                      ORDER BY te.type FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [events]
        FROM sys.triggers AS t
        WHERE t.parent_id = @id
        ORDER BY t.name;
        """;

    /// <summary>
    /// The foreign-key join paths, one row per column pair: every one, or those touching <c>@table</c> (which
    /// <c>OBJECT_ID</c> resolves; a name it does not resolve returns none — the tool checks first).
    /// </summary>
    public const string Relationships =
        """
        SELECT fk.name AS [constraint],
               SCHEMA_NAME(p.schema_id) + '.' + p.name AS [from_table], pc.name AS [from_column],
               SCHEMA_NAME(r.schema_id) + '.' + r.name AS [to_table], rc.name AS [to_column]
        FROM sys.foreign_keys AS fk
        JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.objects AS p ON p.object_id = fkc.parent_object_id
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.objects AS r ON r.object_id = fkc.referenced_object_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE @id IS NULL OR fk.parent_object_id = @id OR fk.referenced_object_id = @id
        ORDER BY [from_table], fk.name, fkc.constraint_column_id
        """;

    /// <summary>
    /// The columns whose name is <c>LIKE @pattern</c> across every user table and view, <c>@schema</c> narrowing it
    /// (later on 2026-09-23, <c>sql_columns</c>): where a column lives, its type parts, nullability, description.
    /// </summary>
    public const string Columns =
        """
        SELECT SCHEMA_NAME(o.schema_id) + '.' + o.name AS [table], CASE o.type WHEN 'U' THEN 'table' ELSE 'view' END AS [kind],
               c.name AS [column], TYPE_NAME(c.user_type_id) AS [type], c.max_length, c.precision, c.scale, c.is_nullable,
               CAST(ep.value AS nvarchar(400)) AS [description]
        FROM sys.columns AS c
        JOIN sys.objects AS o ON o.object_id = c.object_id
        LEFT JOIN sys.extended_properties AS ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND c.name LIKE @pattern
          AND (@schema IS NULL OR SCHEMA_NAME(o.schema_id) = @schema)
        ORDER BY [table], c.column_id
        """;

    /// <summary>
    /// The indexes of every user table and view, or of the object <c>@id</c>, or of <c>@schema</c> (later on
    /// 2026-09-23, <c>sql_indexes</c>): kind and flags, key and included columns, filter, fill factor and size —
    /// catalog views alone, so any login that sees the tables may run it. The last two columns are the ids
    /// <see cref="IndexUsage"/> joins on.
    /// </summary>
    public const string Indexes =
        """
        SELECT SCHEMA_NAME(o.schema_id) + '.' + o.name AS [table], i.name AS [index], i.type_desc AS [kind],
               i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled,
               STUFF((SELECT ', ' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                      FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 AND ic.key_ordinal > 0
                      ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [keys],
               STUFF((SELECT ', ' + c.name
                      FROM sys.index_columns AS ic JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                      ORDER BY ic.index_column_id FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '') AS [included],
               i.filter_definition AS [filter], i.fill_factor,
               (SELECT SUM(a.used_pages) * 8 FROM sys.partitions AS p JOIN sys.allocation_units AS a ON a.container_id = p.partition_id
                WHERE p.object_id = i.object_id AND p.index_id = i.index_id) AS [size_kb],
               i.object_id, i.index_id
        FROM sys.indexes AS i
        JOIN sys.objects AS o ON o.object_id = i.object_id
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND i.type > 0
          AND (@id IS NULL OR i.object_id = @id)
          AND (@schema IS NULL OR SCHEMA_NAME(o.schema_id) = @schema)
        ORDER BY [table], i.index_id
        """;

    /// <summary>
    /// How the indexes have been used since the server last started: <c>sys.dm_db_index_usage_stats</c> for this
    /// database (and <c>@id</c>). A DMV: the login needs <c>VIEW SERVER STATE</c> (<c>VIEW SERVER PERFORMANCE
    /// STATE</c> on 2022); without it the tool shows the indexes and says why the usage is missing.
    /// </summary>
    public const string IndexUsage =
        """
        SELECT s.object_id, s.index_id, s.user_seeks, s.user_scans, s.user_lookups, s.user_updates
        FROM sys.dm_db_index_usage_stats AS s
        WHERE s.database_id = DB_ID() AND (@id IS NULL OR s.object_id = @id)
        """;

    /// <summary>
    /// The optimizer's missing-index suggestions for this database (and <c>@id</c>), the 25 with the most estimated
    /// benefit (cost × impact × uses) — hints the server collected since it started, not a design.
    /// </summary>
    public const string MissingIndexes =
        """
        SELECT TOP 25 OBJECT_SCHEMA_NAME(d.object_id) + '.' + OBJECT_NAME(d.object_id) AS [table],
               d.equality_columns, d.inequality_columns, d.included_columns,
               gs.user_seeks + gs.user_scans AS [uses], CAST(gs.avg_user_impact AS decimal(5, 1)) AS [impact]
        FROM sys.dm_db_missing_index_details AS d
        JOIN sys.dm_db_missing_index_groups AS g ON g.index_handle = d.index_handle
        JOIN sys.dm_db_missing_index_group_stats AS gs ON gs.group_handle = g.index_group_handle
        WHERE d.database_id = DB_ID() AND (@id IS NULL OR d.object_id = @id)
        ORDER BY gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans) DESC
        """;
}
