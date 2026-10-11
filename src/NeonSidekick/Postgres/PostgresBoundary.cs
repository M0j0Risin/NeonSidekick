using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// The lock that holds a PostgreSQL connection to its database and, when its entry names one, its schema (2026-10-09, the user's call,
/// SQL Server's <see cref="SqlBoundary"/> carried over; the schema lock the user's pick the same day). Checked on the model's own text —
/// <c>postgres_query</c>, and <c>postgres_execute</c>'s writes and reads alike — after the gate, before the statement or the allow pane.
/// A session cannot leave its database (a three-part name in another is the server's own error, and <c>dblink</c> is refused by the
/// gates), so what is refused here is what describes the whole server rather than the database (<see cref="ServerCatalogs"/>: the other
/// databases, the roles and their passwords, the settings, other sessions' queries, the user mappings' passwords) whatever the lock;
/// and, under a schema lock, a qualifier naming another schema: a lexer cannot tell <c>sales.orders</c> from an alias's <c>o.id</c>, so
/// the tools read the schemas' names first (<see cref="Names"/>) and a qualifier among them is refused — an alias spelled like one too,
/// which the refusal says to rename — and <c>information_schema</c> at once (it lists every schema's tables), and since 2026-10-11 the
/// catalogs that do the same (<see cref="SchemaListings"/>: <c>pg_class</c>, <c>pg_tables</c>…). <c>pg_catalog</c> stays: its functions are
/// the language's own. What the text cannot show (a view, function or foreign table that reads elsewhere) is the
/// role's to guard. Pure.
/// </summary>
public static class PostgresBoundary
{
    /// <summary>The views and functions of the whole cluster rather than the database. Case-blind.</summary>
    public static readonly IReadOnlySet<string> ServerCatalogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pg_database", "pg_roles", "pg_user", "pg_authid", "pg_shadow", "pg_group", "pg_auth_members", "pg_db_role_setting", "pg_tablespace",
        "pg_settings", "pg_file_settings", "pg_hba_file_rules", "pg_ident_file_mappings", "pg_stat_activity", "pg_stat_database",
        "pg_stat_database_conflicts", "pg_stat_replication", "pg_stat_ssl", "pg_stat_gssapi", "pg_stat_wal_receiver", "pg_replication_slots",
        "pg_user_mappings", "pg_user_mapping", "pg_subscription", "pg_shdescription", "pg_shdepend", "pg_shseclabel",
        "pg_database_size", "pg_tablespace_size", "pg_tablespace_databases", "has_database_privilege", "pg_stat_get_activity",
        // 2026-10-11, the review: other sessions' query text (pg_stat_statements and its kin, as pg_stat_activity), their locks and
        // prepared transactions, and the functions that name or test the cluster's roles (pg_get_userbyid over a series lists them).
        "pg_stat_statements", "pg_stat_statements_info", "pg_stat_monitor", "pg_locks", "pg_prepared_xacts", "pg_stat_subscription",
        "pg_get_userbyid", "pg_has_role",
    };

    /// <summary>
    /// The catalogs that list the objects of every schema in the database, and the functions that describe an object named in their
    /// argument (2026-10-11, the review: under a lock to sales, <c>SELECT tablename FROM pg_tables</c> listed payroll's tables — the reason
    /// <c>information_schema</c> was refused). Refused under a schema lock; without one they stay in the connection's database. Case-blind.
    /// </summary>
    public static readonly IReadOnlySet<string> SchemaListings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pg_namespace", "pg_class", "pg_tables", "pg_views", "pg_matviews", "pg_indexes", "pg_sequences", "pg_sequence", "pg_proc",
        "pg_attribute", "pg_attrdef", "pg_constraint", "pg_index", "pg_trigger", "pg_rewrite", "pg_policy", "pg_policies", "pg_depend",
        "pg_description", "pg_inherits", "pg_partitioned_table", "pg_statistic", "pg_statistic_ext", "pg_statistic_ext_data", "pg_stats",
        "pg_stats_ext", "pg_stats_ext_exprs", "pg_foreign_table", "pg_publication_tables", "pg_publication_rel", "pg_type",
        "pg_stat_all_tables", "pg_stat_user_tables", "pg_stat_sys_tables", "pg_stat_xact_all_tables", "pg_stat_xact_user_tables",
        "pg_stat_all_indexes", "pg_stat_user_indexes", "pg_stat_sys_indexes", "pg_stat_user_functions", "pg_stat_xact_user_functions",
        "pg_statio_all_tables", "pg_statio_user_tables", "pg_statio_sys_tables", "pg_statio_all_indexes", "pg_statio_user_indexes",
        "pg_statio_sys_indexes", "pg_statio_all_sequences", "pg_statio_user_sequences", "pg_statio_sys_sequences",
        "pg_get_viewdef", "pg_get_functiondef", "pg_get_indexdef", "pg_get_triggerdef", "pg_get_ruledef", "pg_get_constraintdef",
    };

    /// <summary>The function families of other sessions: <c>pg_stat_get_backend_*</c>.</summary>
    public static readonly IReadOnlyList<string> ServerCatalogPrefixes = ["pg_stat_get_backend_"];

    /// <summary>The qualifiers a schema lock lets stand beside its own: the language's catalog and the session's temporary schema.</summary>
    public static readonly IReadOnlySet<string> AlwaysSchemas = new HashSet<string>(StringComparer.Ordinal) { "pg_catalog", "pg_temp" };

    /// <summary>
    /// The functions that read a table, a schema or the whole database named in a string (the review, 2026-10-09:
    /// <c>schema_to_xml('payroll', …)</c> read another schema under a lock, no qualifier in sight). Refused under a schema lock; without
    /// one they stay in the connection's database.
    /// </summary>
    public static readonly IReadOnlySet<string> XmlExports = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "table_to_xml", "table_to_xmlschema", "table_to_xml_and_xmlschema", "schema_to_xml", "schema_to_xmlschema", "schema_to_xml_and_xmlschema",
        "database_to_xml", "database_to_xmlschema", "database_to_xml_and_xmlschema",
    };

    /// <summary>A name as PostgreSQL reads it: a bare word folded to lower case, a quoted name as written (the review, 2026-10-09: <c>"Sales"</c> is not <c>sales</c>).</summary>
    private static string Fold(GateToken token) => token.Kind == GateTokenKind.Name ? token.Text : token.Text.ToLowerInvariant();

    /// <summary>The schemas' names in the database, read before the model's statement under a schema lock.</summary>
    public const string Names = "SELECT nspname FROM pg_namespace";

    /// <summary>How many names <see cref="Names"/> reads at most.</summary>
    public const int MaxNames = 100_000;

    /// <summary>
    /// What of <paramref name="sql"/> the server must be asked about: under a lock to <paramref name="schema"/>, every part of a dotted
    /// name but its last (<see cref="ServerWriteGate.Prefixes"/>: a three-part name's schema too), as PostgreSQL reads it, but the
    /// schema's own and <see cref="AlwaysSchemas"/> (none for most statements); none without a lock. Or the refusal at once: a server
    /// catalog, or under a lock <c>information_schema</c> and the <see cref="XmlExports"/>.
    /// </summary>
    public static (IReadOnlyList<string> Heads, string? Refused) Qualifiers(string sql, string? schema)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (PostgresWriteGate.Lex(sql, out _) is not { } tokens)
        {
            return ([], null);
        }

        foreach (var t in tokens)
        {
            if (t.Kind is GateTokenKind.Word or GateTokenKind.Name
                && (ServerCatalogs.Contains(t.Text) || ServerCatalogPrefixes.Any(p => t.Text.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            {
                return ([], PostgresText.ServerCatalog(t.Text.ToLowerInvariant()));
            }
        }

        if (string.IsNullOrWhiteSpace(schema))
        {
            return ([], null);
        }

        string own = schema.Trim();
        if (tokens.FirstOrDefault(t => t.Kind is GateTokenKind.Word or GateTokenKind.Name && XmlExports.Contains(t.Text)) is { Text: { } export })
        {
            return ([], PostgresText.XmlExport(export.ToLowerInvariant(), own));
        }

        if (tokens.FirstOrDefault(t => t.Kind is GateTokenKind.Word or GateTokenKind.Name && SchemaListings.Contains(t.Text)) is { Text: { } listing })
        {
            return ([], PostgresText.SchemaListing(listing.ToLowerInvariant(), own));
        }

        var heads = ServerWriteGate.Prefixes(tokens).Select(Fold).Where(h => !h.Equals(own, StringComparison.Ordinal) && !AlwaysSchemas.Contains(h)).ToList();
        return heads.Contains("information_schema")
            ? (heads, PostgresText.OtherSchema("information_schema", own))
            : (heads, null);
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to the connection's database and <paramref name="schema"/> (null for no lock), the schemas
    /// in the database being <paramref name="visible"/>; else the <c>Error:</c> sentence. A text that does not lex passes: the gate refused it.
    /// </summary>
    public static string? Check(string sql, string? schema, IEnumerable<string> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        var (heads, refused) = Qualifiers(sql, schema);
        if (refused is not null || heads.Count == 0)
        {
            return refused;
        }

        // The heads are folded as PostgreSQL folds them, so the schemas' names compare exactly.
        var others = new HashSet<string>(visible, StringComparer.Ordinal);
        return heads.FirstOrDefault(others.Contains) is { } head ? PostgresText.OtherSchema(head, schema!.Trim()) : null;
    }
}
