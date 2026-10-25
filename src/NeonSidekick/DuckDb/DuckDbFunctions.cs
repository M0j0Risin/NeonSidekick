namespace NeonSidekick.DuckDb;

/// <summary>How the gates treat a table function or table macro of the bundled engine (<see cref="DuckDbFunctions"/>).</summary>
public enum DuckDbFunctionClass
{
    /// <summary>Reads files: allowed only with a string literal (or a list of them) as its first argument, each checked as a file reference.</summary>
    Reader,

    /// <summary>Reads nothing outside the database and changes nothing.</summary>
    Harmless,

    /// <summary>Refused wherever it is called.</summary>
    Denied,
}

/// <summary>
/// Every table function and table macro the bundled DuckDB (1.5.6) has, each in one of three lists (2026-10-25, the DuckDB plan's
/// spike item 7): <c>sniff_csv</c> once read files with outside access off (CVE-2024-41672), so the gates classify every one rather than
/// keep a deny-list, and <c>DuckDbTests</c> reads <c>duckdb_functions()</c> from the engine and fails on one in no list — a DuckDB upgrade
/// that adds a reader cannot slip through. A name in no list (a table macro the database defines) is the user's own and passes; the
/// engine's folder rule holds whatever it reads. Names upper-cased, as the lexer gives words.
/// </summary>
public static class DuckDbFunctions
{
    /// <summary>The readers: a file (or a list of them, or a glob) as the first argument.</summary>
    public static readonly IReadOnlySet<string> Readers = new HashSet<string>(StringComparer.Ordinal)
    {
        "READ_CSV", "READ_CSV_AUTO", "SNIFF_CSV",
        "READ_PARQUET", "PARQUET_SCAN", "PARQUET_METADATA", "PARQUET_FILE_METADATA", "PARQUET_FULL_METADATA", "PARQUET_KV_METADATA",
        "PARQUET_SCHEMA", "PARQUET_BLOOM_PROBE",
        "READ_JSON", "READ_JSON_AUTO", "READ_JSON_OBJECTS", "READ_JSON_OBJECTS_AUTO", "READ_NDJSON", "READ_NDJSON_AUTO", "READ_NDJSON_OBJECTS",
        "READ_TEXT", "READ_BLOB",
    };

    /// <summary>The ones that read nothing outside the database: the catalog's views of itself, series, and the like.</summary>
    public static readonly IReadOnlySet<string> Harmless = new HashSet<string>(StringComparer.Ordinal)
    {
        "DUCKDB_APPROX_DATABASE_COUNT", "DUCKDB_COLUMNS", "DUCKDB_CONNECTION_COUNT", "DUCKDB_CONSTRAINTS", "DUCKDB_COORDINATE_SYSTEMS",
        "DUCKDB_DATABASES", "DUCKDB_DEPENDENCIES", "DUCKDB_EXTENSIONS", "DUCKDB_EXTERNAL_FILE_CACHE", "DUCKDB_FUNCTIONS", "DUCKDB_INDEXES",
        "DUCKDB_KEYWORDS", "DUCKDB_LOG_CONTEXTS", "DUCKDB_LOGS", "DUCKDB_LOGS_PARSED", "DUCKDB_MEMORY", "DUCKDB_OPTIMIZERS",
        "DUCKDB_PREPARED_STATEMENTS", "DUCKDB_PROFILING_SETTINGS", "DUCKDB_SCHEMAS", "DUCKDB_SECRET_TYPES", "DUCKDB_SEQUENCES",
        "DUCKDB_SETTINGS", "DUCKDB_TABLE_SAMPLE", "DUCKDB_TABLES", "DUCKDB_TEMPORARY_FILES", "DUCKDB_TYPES", "DUCKDB_VARIABLES",
        "DUCKDB_VIEWS", "GENERATE_SERIES", "RANGE", "UNNEST", "REPEAT", "REPEAT_ROW", "JSON_EACH", "JSON_TREE", "ICU_CALENDAR_NAMES",
        "PG_TIMEZONE_NAMES", "PRAGMA_COLLATIONS", "PRAGMA_DATABASE_SIZE", "PRAGMA_METADATA_INFO", "PRAGMA_PLATFORM", "PRAGMA_STORAGE_INFO",
        "PRAGMA_TABLE_INFO", "PRAGMA_USER_AGENT", "PRAGMA_VERSION", "SQL_AUTO_COMPLETE", "SUMMARY", "TEST_ALL_TYPES", "TEST_VECTOR_TYPES",
    };

    /// <summary>
    /// The refused ones: a query handed over as text (<c>query</c>, <c>query_table</c>, <c>json_execute_serialized_sql</c>, and the
    /// <c>histogram</c> macros built on <c>query_table</c>), a file listing (<c>glob</c>), another database file read whole
    /// (<c>read_duckdb</c>), the secrets, a raw pointer (<c>arrow_scan</c>), a checkpoint, the logging, profiling and parser switches,
    /// and <c>pragma_show</c> (DESCRIBE is the way, and it names a table as text).
    /// </summary>
    public static readonly IReadOnlySet<string> Denied = new HashSet<string>(StringComparer.Ordinal)
    {
        "QUERY", "QUERY_TABLE", "JSON_EXECUTE_SERIALIZED_SQL", "HISTOGRAM", "HISTOGRAM_VALUES", "GLOB", "READ_DUCKDB", "DUCKDB_SECRETS",
        "WHICH_SECRET", "ARROW_SCAN", "ARROW_SCAN_DUMB", "SEQ_SCAN", "CHECKPOINT", "FORCE_CHECKPOINT", "ENABLE_LOGGING", "DISABLE_LOGGING",
        "TRUNCATE_DUCKDB_LOGS", "ENABLE_PROFILING", "DISABLE_PROFILING", "ENABLE_PEG_PARSER", "DISABLE_PEG_PARSER", "CHECK_PEG_PARSER",
        "PRAGMA_SHOW",
    };

    /// <summary>Scalar functions a statement may not call: a sequence moved (no rollback undoes it), or a serialized query run.</summary>
    public static readonly IReadOnlySet<string> DeniedScalars = new HashSet<string>(StringComparer.Ordinal)
    {
        "NEXTVAL", "SETVAL", "GETENV",
    };

    /// <summary>The scalars <c>duckdb_execute</c> may not call: a write may move a sequence, and it asks first.</summary>
    public static readonly IReadOnlySet<string> WriteDeniedScalars = new HashSet<string>(StringComparer.Ordinal)
    {
        "GETENV",
    };

    /// <summary>The class of a table function by its upper-cased name; null when it is none of the engine's.</summary>
    public static DuckDbFunctionClass? Of(string upper) =>
        Readers.Contains(upper) ? DuckDbFunctionClass.Reader
        : Denied.Contains(upper) ? DuckDbFunctionClass.Denied
        : Harmless.Contains(upper) ? DuckDbFunctionClass.Harmless
        : null;

    /// <summary>The readers the tools' descriptions and rule name: the ones a model reaches for.</summary>
    public const string ReaderNames = "read_csv, read_parquet, read_json, read_text, read_blob";
}
