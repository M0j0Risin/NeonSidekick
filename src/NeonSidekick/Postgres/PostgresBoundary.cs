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
        // 2026-10-24, the database review's B2, R3, C6, R1 (postgresql.org/docs/current/functions-admin.html, functions-info.html,
        // monitoring-stats.html, view-pg-*.html): the functions behind the refused views, the progress views of other sessions, the
        // replication origins and slots' statistics, the parameter ACLs and security labels, a tablespace's path, the user mappings'
        // options (their passwords), the shared objects' comments, and the casts and functions that turn an oid into a role's name.
        "pg_lock_status", "pg_show_all_settings", "pg_show_all_file_settings", "pg_get_replication_slots", "pg_prepared_xact", "current_setting",
        "pg_stat_get_progress_info", "pg_blocking_pids", "pg_safe_snapshot_blocking_pids", "pg_stat_replication_slots", "pg_stat_subscription_stats",
        "pg_replication_origin", "pg_replication_origin_status", "pg_parameter_acl", "pg_seclabels", "pg_tablespace_location",
        "user_mapping_options", "user_mappings", "shobj_description", "regrole", "to_regrole",
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

    /// <summary>The function families of other sessions and the cluster: <c>pg_stat_get_backend_*</c>, <c>pg_stat_get_db_*</c> and the progress views (R3).</summary>
    public static readonly IReadOnlyList<string> ServerCatalogPrefixes = ["pg_stat_get_backend_", "pg_stat_get_db_", "pg_stat_progress_", "pg_replication_origin_"];

    /// <summary>
    /// The <c>pg_</c>-named functions a query may call under a schema lock (2026-10-24, the database review's DB2, from Phase 0's sweep of
    /// postgresql.org/docs/current/functions-*.html): they describe a value, the session or the server's clock, never another object. Every
    /// other <c>pg_</c> relation or function — the catalogs, <c>pg_get_*def</c>, <c>pg_describe_object</c>, the size functions over a
    /// <c>regclass</c>, <c>pg_*_is_visible</c>, the advisory locks — is refused under a lock unless the lock's own schema qualifies it (a
    /// user's table may be named <c>pg_notes</c>). <c>pg_snapshot_*</c> is a family.
    /// </summary>
    public static readonly IReadOnlySet<string> LockAllowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "pg_catalog", "pg_temp", "pg_typeof", "pg_column_size", "pg_size_pretty", "pg_size_bytes", "pg_backend_pid", "pg_client_encoding",
        "pg_encoding_to_char", "pg_char_to_encoding", "pg_trigger_depth", "pg_jit_available", "pg_is_in_recovery", "pg_postmaster_start_time",
        "pg_conf_load_time", "pg_current_xact_id_if_assigned", "pg_current_snapshot", "pg_xact_status", "pg_visible_in_snapshot",
    };

    /// <summary>
    /// The readers with no <c>pg_</c> in their name that turn an oid or a name into another schema's objects (DB2, R1, F14): refused under a
    /// lock, as the <c>pg_</c> ones are. <c>has_*_privilege</c>, <c>to_reg*</c> and the <c>reg*</c> types are families.
    /// </summary>
    public static readonly IReadOnlySet<string> LockReaders = new HashSet<string>(StringComparer.Ordinal)
    {
        "obj_description", "col_description", "format_type", "acldefault", "aclexplode",   // current_schemas names the session's own path, the lock's
        "regclass", "regtype", "regproc", "regprocedure", "regoper", "regoperator", "regnamespace", "regconfig", "regdictionary", "regcollation",
    };

    /// <summary>The functions whose first argument names a sequence as text (R2: <c>setval('payroll.seq', 1)</c> moved another schema's).</summary>
    public static readonly IReadOnlySet<string> SequenceFunctions = new HashSet<string>(StringComparer.Ordinal) { "nextval", "currval", "setval" };

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

    /// <summary>
    /// A name as PostgreSQL reads it: a bare word folded to lower case, A to Z alone (2026-10-24, review B3), a quoted name as written (the
    /// review, 2026-10-09: <c>"Sales"</c> is not <c>sales</c>).
    /// </summary>
    private static string Fold(GateToken token) => token.Kind == GateTokenKind.Name ? token.Text : PostgresReadOnlyGate.AsciiLower(token.Text);

    /// <summary>The schemas' names in the database, read before the model's statement under a schema lock.</summary>
    public const string Names = "SELECT nspname FROM pg_namespace";

    /// <summary>How many names <see cref="Names"/> reads at most.</summary>
    public const int MaxNames = 100_000;

    /// <summary><see cref="Names"/>' rows as the set <see cref="Check"/> compares with, built once per read (2026-10-24, the database review's R14).</summary>
    public static IReadOnlySet<string> NameSet(IReadOnlyList<string[]> rows) => new HashSet<string>(rows.Select(r => r[0]), StringComparer.Ordinal);

    /// <summary>
    /// What of <paramref name="sql"/> the server must be asked about: under a lock to <paramref name="schema"/>, every part of a dotted
    /// name but its last (<see cref="ServerWriteGate.Prefixes"/>: a three-part name's schema too), as PostgreSQL reads it, but the
    /// schema's own and <see cref="AlwaysSchemas"/> (none for most statements); none without a lock. Or the refusal at once: a server
    /// catalog, or under a lock <c>information_schema</c> and the <see cref="XmlExports"/>.
    /// </summary>
    public static (IReadOnlyList<string> Heads, string? Refused) Qualifiers(string sql, string? schema)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (PostgresWriteGate.LexWithBodies(sql, out _) is not { } tokens)
        {
            // A text the boundary cannot read is refused (2026-10-24, mechanism 7): it passed here "because the gate refused it", which
            // held only while every caller ran the gate first.
            return ([], PostgresText.BoundaryUnread);
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

        if (LockRefusal(tokens, own) is { } locked)
        {
            return ([], locked);
        }

        var heads = ServerWriteGate.Prefixes(tokens).Select(Fold).Where(h => !h.Equals(own, StringComparison.Ordinal) && !AlwaysSchemas.Contains(h)).ToList();

        // A bare schema name (G2, A11): DROP SCHEMA payroll, ALTER SCHEMA payroll RENAME, COMMENT ON SCHEMA payroll, ALTER TABLE t SET
        // SCHEMA payroll, CREATE SCHEMA scratch — the word after SCHEMA (past IF [NOT] EXISTS) is a schema whatever the names say.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("SCHEMA") || (i > 0 && tokens[i - 1].IsSymbol(".")))
            {
                continue;
            }

            int at = i + 1;
            while (at < tokens.Count && tokens[at].Kind == GateTokenKind.Word && tokens[at].Text is "IF" or "NOT" or "EXISTS")
            {
                at++;
            }

            if (at < tokens.Count && tokens[at].Kind is GateTokenKind.Word or GateTokenKind.Name && Fold(tokens[at]) is { } named
                && !named.Equals(own, StringComparison.Ordinal) && !(i > 0 && tokens[i - 1].IsWord("CURRENT")))
            {
                return (heads, PostgresText.OtherSchema(named, own));
            }
        }

        return heads.Contains("information_schema")
            ? (heads, PostgresText.OtherSchema("information_schema", own))
            : (heads, null);
    }

    /// <summary>
    /// What a schema lock refuses before any names are read (2026-10-24, the database review's DB2, R1, R2, R4, F14, H8): a <c>pg_</c>
    /// relation or function not in <see cref="LockAllowed"/> and a <see cref="LockReaders"/> one (<c>has_*_privilege</c>, <c>to_reg*</c>
    /// too), unless the lock's own schema qualifies the name; a sequence function handed another schema's sequence as text; and
    /// <c>CASCADE</c>, which drops or empties what depends on the object wherever it is.
    /// </summary>
    private static string? LockRefusal(IReadOnlyList<GateToken> tokens, string own)
    {
        int lead = tokens.ToList().FindIndex(t => !t.IsSymbol("("));
        if (lead >= 0 && PostgresWriteGate.WholeDatabase(tokens, lead) is { } whole)
        {
            return PostgresText.WholeUnderLock(whole, own);   // H8: upkeep over every schema's tables
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is not (GateTokenKind.Word or GateTokenKind.Name))
            {
                continue;
            }

            string name = Fold(t);
            bool qualifiedByOwn = i >= 2 && tokens[i - 1].IsSymbol(".") && tokens[i - 2].Kind is GateTokenKind.Word or GateTokenKind.Name
                && Fold(tokens[i - 2]).Equals(own, StringComparison.Ordinal);
            bool member = i >= 2 && tokens[i - 1].IsSymbol(".") && !(tokens[i - 2].Kind is GateTokenKind.Word or GateTokenKind.Name && AlwaysSchemas.Contains(Fold(tokens[i - 2])));
            if (qualifiedByOwn || (member && !name.StartsWith("pg_", StringComparison.Ordinal)))
            {
                continue;   // the lock's own object, or a column or member after another qualifier (which the names judge)
            }

            bool reader = name.StartsWith("pg_", StringComparison.Ordinal) ? !LockAllowed.Contains(name) && !name.StartsWith("pg_snapshot_", StringComparison.Ordinal)
                : LockReaders.Contains(name) || name.StartsWith("to_reg", StringComparison.Ordinal)
                  || (name.StartsWith("has_", StringComparison.Ordinal) && name.EndsWith("_privilege", StringComparison.Ordinal));
            if (reader)
            {
                return PostgresText.CatalogUnderLock(name, own);
            }

            if (t.IsWord("CASCADE") && !(i > 0 && (tokens[i - 1].IsWord("DELETE") || tokens[i - 1].IsWord("UPDATE"))))   // a key's ON DELETE CASCADE stays
            {
                return PostgresText.CascadeUnderLock(own);
            }

            if (SequenceFunctions.Contains(name) && i + 2 < tokens.Count && tokens[i + 1].IsSymbol("(") && tokens[i + 2].Kind == GateTokenKind.Other
                && PostgresWriteGate.StringValue(tokens[i + 2].Text) is { } sequence && SequenceSchema(sequence) is { } named
                && !named.Equals(own, StringComparison.Ordinal) && !AlwaysSchemas.Contains(named))
            {
                return PostgresText.OtherSchema(named, own);
            }
        }

        return null;
    }

    /// <summary>The schema part of a sequence's name written as text (<c>payroll.seq</c>, <c>"Pay".seq</c>), as the server reads it; null for none.</summary>
    private static string? SequenceSchema(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            return close > 0 && close + 1 < text.Length && text[close + 1] == '.' ? text[1..close] : null;
        }

        int dot = text.IndexOf('.');
        return dot > 0 ? PostgresReadOnlyGate.AsciiLower(text[..dot]) : null;
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to the connection's database and <paramref name="schema"/> (null for no lock), the schemas
    /// in the database being <paramref name="visible"/>; else the <c>Error:</c> sentence. A text that does not lex is refused (mechanism 7).
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
        var others = visible as IReadOnlySet<string> ?? new HashSet<string>(visible, StringComparer.Ordinal);   // a set the names cache built (R14)
        return heads.FirstOrDefault(h => others.Contains(SqlText.Cell(h))) is { } head ? PostgresText.OtherSchema(head, schema!.Trim()) : null;   // as the names list writes it: a cell, scrubbed, NULL quoted (2026-10-24, the review of these fixes)
    }
}
