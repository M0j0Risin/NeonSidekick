namespace NeonSidekick.Postgres;

/// <summary>
/// The PostgreSQL tools' own catalog statements (2026-10-04): <c>pg_catalog</c> reads that run under the same read-only session as a
/// query, every value bound (<c>@schema</c>, <c>@name</c>, <c>@pattern</c>, <c>@table</c>; cast to <c>text</c> so a null binds), the
/// internal schemas (<c>pg_*</c>, <c>information_schema</c>) left out, and only what the account may read
/// (<c>has_table_privilege</c>, <c>has_schema_privilege</c>, <c>has_database_privilege</c>).
/// </summary>
public static class PostgresCatalogQueries
{
    private const string UserSchemas = "n.nspname NOT LIKE 'pg\\_%' AND n.nspname <> 'information_schema'";

    private const string Kind =
        "CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'partitioned table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'f' THEN 'foreign table' END";

    /// <summary>The relation <c>@schema.@name</c> names, quoted as written.</summary>
    private const string Target = "to_regclass(format('%I.%I', @schema::text, @name::text))";

    /// <summary>A constraint's columns on one side, in key order: <paramref name="keys"/> of <paramref name="relation"/>.</summary>
    private static string Columns(string keys, string relation) =>
        $"(SELECT string_agg(a.attname, ', ' ORDER BY k.n) FROM unnest(con.{keys}) WITH ORDINALITY k(attnum, n) JOIN pg_attribute a ON a.attrelid = con.{relation} AND a.attnum = k.attnum)";

    /// <summary>Who the session is, where, the server; then what the role may do beyond reading (superuser, role and database creation, RLS bypass, owned tables, write grants).</summary>
    public static readonly IReadOnlyList<string> WhoAmI =
    [
        "SELECT current_user, current_database(), version()",
        "SELECT p FROM (SELECT 'superuser' AS p FROM pg_roles WHERE rolname = current_user AND rolsuper " +
            "UNION ALL SELECT 'CREATEROLE' FROM pg_roles WHERE rolname = current_user AND rolcreaterole " +
            "UNION ALL SELECT 'CREATEDB' FROM pg_roles WHERE rolname = current_user AND rolcreatedb " +
            "UNION ALL SELECT 'BYPASSRLS' FROM pg_roles WHERE rolname = current_user AND rolbypassrls " +
            "UNION ALL SELECT 'owns ' || count(*) || ' tables' FROM pg_tables WHERE tableowner = current_user HAVING count(*) > 0 " +
            "UNION ALL SELECT DISTINCT privilege_type FROM information_schema.table_privileges WHERE grantee = current_user AND privilege_type IN ('INSERT', 'UPDATE', 'DELETE', 'TRUNCATE')) w",
    ];

    public const string Databases =
        "SELECT datname AS database, pg_size_pretty(pg_database_size(datname)) AS size, pg_encoding_to_char(encoding) AS encoding " +
        "FROM pg_database WHERE datallowconn AND NOT datistemplate AND has_database_privilege(datname, 'CONNECT') ORDER BY datname";

    public const string Schemas =
        "SELECT n.nspname AS schema, count(c.oid) FILTER (WHERE c.relkind IN ('r','p','v','m','f')) AS tables, pg_get_userbyid(n.nspowner) AS owner " +
        "FROM pg_namespace n LEFT JOIN pg_class c ON c.relnamespace = n.oid WHERE " + UserSchemas + " AND has_schema_privilege(n.oid, 'USAGE') " +
        "GROUP BY n.nspname, n.nspowner ORDER BY n.nspname";

    public const string Tables =
        "SELECT n.nspname || '.' || c.relname AS name, " + Kind + " AS kind, CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END AS rows, " +
        "obj_description(c.oid, 'pg_class') AS description FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
        "WHERE c.relkind IN ('r','p','v','m','f') AND " + UserSchemas + " AND has_table_privilege(c.oid, 'SELECT') " +
        "AND (@schema::text IS NULL OR n.nspname = @schema::text) " +
        "AND (@pattern::text IS NULL OR c.relname ILIKE @pattern::text OR n.nspname || '.' || c.relname ILIKE @pattern::text) ORDER BY 1";

    public const string ColumnSearch =
        "SELECT n.nspname || '.' || c.relname AS \"table\", a.attname AS \"column\", format_type(a.atttypid, a.atttypmod) AS type, " +
        "NOT a.attnotnull AS nullable, col_description(c.oid, a.attnum) AS description FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r','p','v','m','f') AND " + UserSchemas + " " +
        "AND has_table_privilege(c.oid, 'SELECT') AND (@schema::text IS NULL OR n.nspname = @schema::text) AND a.attname ILIKE @pattern::text ORDER BY 1, a.attnum";

    /// <summary>The relations <c>@name</c> may mean (case-insensitive; <c>@schema</c> when given): schema, name, kind, whether the case matched, whether its schema is on the search path.</summary>
    public const string Resolve =
        "SELECT n.nspname, c.relname, " + Kind + ", c.relname = @name::text, n.nspname = ANY(current_schemas(false)) FROM pg_class c " +
        "JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r','p','v','m','f') AND " + UserSchemas + " " +
        "AND lower(c.relname) = lower(@name::text) AND (@schema::text IS NULL OR lower(n.nspname) = lower(@schema::text)) ORDER BY 1, 2";

    /// <summary>
    /// <c>@schema.@name</c>'s columns (a key column's place counted from 1: <c>indkey</c> is an <c>int2vector</c>, zero-based, found live on
    /// 2026-10-04), foreign keys out, keys pointing in, indexes, CHECK constraints and comment: six result sets in that order.
    /// </summary>
    public static readonly IReadOnlyList<string> Describe =
    [
        "SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid), array_position(i.indkey::int2[], a.attnum) - array_lower(i.indkey::int2[], 1) + 1, " +
            "col_description(a.attrelid, a.attnum) FROM pg_attribute a LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum " +
            "LEFT JOIN pg_index i ON i.indrelid = a.attrelid AND i.indisprimary WHERE a.attrelid = " + Target + " AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum",
        "SELECT con.conname, " + Columns("conkey", "conrelid") + ", con.confrelid::regclass::text, " + Columns("confkey", "confrelid") + " " +
            "FROM pg_constraint con WHERE con.conrelid = " + Target + " AND con.contype = 'f' ORDER BY 1",
        "SELECT con.conrelid::regclass::text, " + Columns("conkey", "conrelid") + ", " + Columns("confkey", "confrelid") + " " +
            "FROM pg_constraint con WHERE con.confrelid = " + Target + " AND con.contype = 'f' ORDER BY 1",
        "SELECT c.relname, pg_get_indexdef(i.indexrelid) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = " + Target + " ORDER BY 1",
        "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = " + Target + " AND contype = 'c' ORDER BY 1",
        "SELECT obj_description(" + Target + ", 'pg_class')",
    ];

    /// <summary>The foreign keys of a schema (or every user schema), or of one table either way (<c>@schema</c>/<c>@name</c> both given): from, its columns, to, its columns, the name.</summary>
    public const string Relationships =
        "SELECT con.conrelid::regclass::text AS \"from\", " + "(SELECT string_agg(a.attname, ', ' ORDER BY k.n) FROM unnest(con.conkey) WITH ORDINALITY k(attnum, n) JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum)" +
        " AS from_columns, con.confrelid::regclass::text AS \"to\", (SELECT string_agg(a.attname, ', ' ORDER BY k.n) FROM unnest(con.confkey) WITH ORDINALITY k(attnum, n) JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)" +
        " AS to_columns, con.conname AS name FROM pg_constraint con JOIN pg_namespace n ON n.oid = con.connamespace WHERE con.contype = 'f' AND " + UserSchemas + " " +
        "AND (@name::text IS NULL AND (@schema::text IS NULL OR n.nspname = @schema::text) OR @name::text IS NOT NULL AND " + Target + " IN (con.conrelid, con.confrelid)) ORDER BY 1, 3";

    /// <summary>The indexes of a schema (or every user schema), or of one table: table, index, unique, primary, the definition, the scans since the statistics were reset.</summary>
    public const string Indexes =
        "SELECT n.nspname || '.' || t.relname AS \"table\", c.relname AS index, i.indisunique AS \"unique\", i.indisprimary AS \"primary\", " +
        "pg_get_indexdef(i.indexrelid) AS definition, s.idx_scan AS scans FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid " +
        "JOIN pg_class t ON t.oid = i.indrelid JOIN pg_namespace n ON n.oid = t.relnamespace LEFT JOIN pg_stat_all_indexes s ON s.indexrelid = i.indexrelid " +
        "WHERE " + UserSchemas + " AND (@name::text IS NULL AND (@schema::text IS NULL OR n.nspname = @schema::text) OR @name::text IS NOT NULL AND t.oid = " + Target + ") ORDER BY 1, 2";
}
