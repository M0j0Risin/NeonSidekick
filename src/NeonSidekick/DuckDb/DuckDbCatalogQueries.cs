namespace NeonSidekick.DuckDb;

/// <summary>
/// The DuckDB tools' own catalog statements (2026-10-25), <c>SqliteCatalogQueries</c>' twin over DuckDB's catalog functions
/// (<c>duckdb_tables()</c>, <c>duckdb_views()</c>, <c>duckdb_columns()</c>, <c>duckdb_constraints()</c>, <c>duckdb_indexes()</c>), plain SELECTs
/// that run under the same locked, read-only session as a query, each kept to the open database (<c>current_database()</c>) and to the user's
/// own objects (<c>NOT internal</c>). Every name is bound as <c>$name</c> (DuckDB takes no <c>@name</c>), never spliced. Pinned by the tests
/// that run them on real files.
/// </summary>
public static class DuckDbCatalogQueries
{
    /// <summary>The user's tables and views of the open database: schema, name, kind.</summary>
    private const string Objects =
        "SELECT schema_name, table_name AS name, 'table' AS kind FROM duckdb_tables() WHERE database_name = current_database() AND NOT internal " +
        "UNION ALL SELECT schema_name, view_name, 'view' FROM duckdb_views() WHERE database_name = current_database() AND NOT internal";

    /// <summary>
    /// The tables and views: name (bare when every one is in <c>main</c>, else <c>schema.name</c>), kind; <c>$pattern</c> an ILIKE pattern on the
    /// bare name, or null for all.
    /// </summary>
    public const string Tables =
        "WITH o AS (" + Objects + "), n AS (SELECT CASE WHEN bool_and(schema_name = 'main') OVER () THEN name ELSE schema_name || '.' || name END AS shown, kind, schema_name, name FROM o) " +
        "SELECT shown AS name, kind FROM n WHERE $pattern IS NULL OR name ILIKE $pattern ORDER BY schema_name, name";

    /// <summary>The table or view <c>$name</c> means (<c>name</c> or <c>schema.name</c>), case-insensitive: its schema, own spelling and kind; <c>main</c> first.</summary>
    public const string Resolve =
        "SELECT schema_name, name, kind FROM (" + Objects + ") WHERE lower(name) = lower($name) OR lower(schema_name || '.' || name) = lower($name) " +
        "ORDER BY (name = $name OR schema_name || '.' || name = $name) DESC, schema_name = 'main' DESC, schema_name LIMIT 1";

    private const string Of = "database_name = current_database() AND schema_name = $schema AND table_name = $name";

    /// <summary>
    /// <c>$schema.$name</c>'s columns (name, type, nullable, default, primary-key place), its foreign keys out (table, from, to), the keys that
    /// point at it (table, from, to), its indexes (name, unique, expressions), its UNIQUE constraints' columns and its <c>CREATE</c> text — six
    /// result sets in that order.
    /// </summary>
    public static readonly IReadOnlyList<string> Describe =
    [
        "SELECT c.column_name, c.data_type, c.is_nullable, c.column_default, coalesce(list_position(p.cols, c.column_name), 0) AS pk FROM duckdb_columns() c " +
            "LEFT JOIN (SELECT constraint_column_names AS cols FROM duckdb_constraints() WHERE " + Of + " AND constraint_type = 'PRIMARY KEY') p ON true " +
            "WHERE c.database_name = current_database() AND c.schema_name = $schema AND c.table_name = $name ORDER BY c.column_index",
        "SELECT referenced_table, array_to_string(constraint_column_names, ', '), array_to_string(referenced_column_names, ', ') FROM duckdb_constraints() " +
            "WHERE " + Of + " AND constraint_type = 'FOREIGN KEY' ORDER BY constraint_index",
        "SELECT table_name, array_to_string(constraint_column_names, ', '), array_to_string(referenced_column_names, ', ') FROM duckdb_constraints() " +
            "WHERE database_name = current_database() AND constraint_type = 'FOREIGN KEY' AND lower(referenced_table) = lower($name) ORDER BY table_name",
        "SELECT index_name, is_unique, expressions FROM duckdb_indexes() WHERE " + Of + " ORDER BY index_name",
        "SELECT array_to_string(constraint_column_names, ', ') FROM duckdb_constraints() WHERE " + Of + " AND constraint_type = 'UNIQUE' ORDER BY constraint_index",
        "SELECT sql FROM duckdb_tables() WHERE " + Of + " UNION ALL SELECT sql FROM duckdb_views() WHERE database_name = current_database() AND schema_name = $schema AND view_name = $name",
    ];

    /// <summary>How many tables and views the file holds: the wizard's and the check's proof that it opened as a database.</summary>
    public const string Count = "SELECT count(*) FROM (" + Objects + ")";
}
