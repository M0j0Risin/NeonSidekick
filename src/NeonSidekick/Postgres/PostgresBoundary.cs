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
/// which the refusal says to rename — and <c>information_schema</c> at once (it lists every schema's tables). <c>pg_catalog</c> stays:
/// its functions are the language's own. What the text cannot show (a view, function or foreign table that reads elsewhere) is the
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
    };

    /// <summary>The function families of other sessions: <c>pg_stat_get_backend_*</c>.</summary>
    public static readonly IReadOnlyList<string> ServerCatalogPrefixes = ["pg_stat_get_backend_"];

    /// <summary>The qualifiers a schema lock lets stand beside its own: the language's catalog and the session's temporary schema.</summary>
    public static readonly IReadOnlySet<string> AlwaysSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pg_catalog", "pg_temp" };

    /// <summary>The schemas' names in the database, read before the model's statement under a schema lock.</summary>
    public const string Names = "SELECT nspname FROM pg_namespace";

    /// <summary>How many names <see cref="Names"/> reads at most.</summary>
    public const int MaxNames = 100_000;

    /// <summary>
    /// What of <paramref name="sql"/> the server must be asked about: under a lock to <paramref name="schema"/>, its qualifiers but the
    /// schema's own and <see cref="AlwaysSchemas"/> (none for most statements); none without a lock. Or the refusal at once: a server
    /// catalog, or <c>information_schema</c> under a lock.
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
        var heads = ServerWriteGate.Qualifiers(tokens).Where(h => !h.Equals(own, StringComparison.OrdinalIgnoreCase) && !AlwaysSchemas.Contains(h)).ToList();
        return heads.FirstOrDefault(h => h.Equals("information_schema", StringComparison.OrdinalIgnoreCase)) is not null
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

        var others = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        if (heads.FirstOrDefault(others.Contains) is not { } head)
        {
            return null;
        }

        // The lexer upper-cases a bare word: name the schema as the database spells it.
        others.TryGetValue(head, out var named);
        return PostgresText.OtherSchema(named ?? head, schema!.Trim());
    }
}
