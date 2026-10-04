namespace NeonSidekick.Sqlite;

/// <summary>
/// The SQLite tools' own catalog statements (2026-10-04): <c>sqlite_master</c> and the table-valued pragma functions
/// (<c>pragma_table_xinfo</c>, <c>pragma_foreign_key_list</c>, <c>pragma_index_list</c>, <c>pragma_index_info</c>), plain SELECTs that
/// run under the same read-only session as a query. A file is small, so <see cref="Describe"/> answers columns, keys both ways,
/// indexes and the <c>CREATE</c> text in one call. Every name is bound, never spliced. Pinned by the tests that run them on real files.
/// </summary>
public static class SqliteCatalogQueries
{
    /// <summary>The tables and views of the main schema (the internal <c>sqlite_</c> ones left out): name, kind; <c>@pattern</c> a LIKE pattern or null for all.</summary>
    public const string Tables =
        "SELECT name, type AS kind FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' " +
        "AND (@pattern IS NULL OR name LIKE @pattern) ORDER BY name";

    /// <summary>The table or view <c>@name</c> means, case-insensitive: its own spelling and kind.</summary>
    public const string Resolve =
        "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name = @name COLLATE NOCASE ORDER BY name = @name DESC LIMIT 1";

    /// <summary>
    /// <c>@name</c>'s columns (name, type, notnull, default, pk, hidden), its foreign keys out (table, from, to, on update, on delete;
    /// a compound key's columns joined), the keys of other tables that point at it (table, from, to), its indexes (name, unique,
    /// origin, partial, columns) and its <c>CREATE</c> text — five result sets in that order.
    /// </summary>
    public static readonly IReadOnlyList<string> Describe =
    [
        "SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo(@name) ORDER BY cid",
        "SELECT \"table\", group_concat(\"from\", ', '), group_concat(\"to\", ', '), on_update, on_delete FROM pragma_foreign_key_list(@name) GROUP BY id ORDER BY id",
        "SELECT m.name, group_concat(f.\"from\", ', '), group_concat(f.\"to\", ', ') FROM sqlite_master m JOIN pragma_foreign_key_list(m.name) f " +
            "WHERE m.type = 'table' AND f.\"table\" = @name COLLATE NOCASE GROUP BY m.name, f.id ORDER BY m.name",
        "SELECT il.name, il.\"unique\", il.origin, il.partial, group_concat(ii.name, ', ') FROM pragma_index_list(@name) il " +
            "JOIN pragma_index_info(il.name) ii GROUP BY il.name ORDER BY il.name",
        "SELECT sql FROM sqlite_master WHERE name = @name",
    ];

    /// <summary>How many tables and views the file holds: the wizard's and the check's proof that it opened as a database.</summary>
    public const string Count = "SELECT count(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\'";
}
