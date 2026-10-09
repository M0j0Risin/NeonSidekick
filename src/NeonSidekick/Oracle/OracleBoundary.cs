using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// The lock that holds an Oracle connection to its one schema (2026-10-09, the user's pick: the configured <c>schema</c>, else the
/// account's own — SQL Server's <see cref="SqlBoundary"/> carried over to Oracle's schemas). Checked on the model's own text —
/// <c>oracle_query</c>, and <c>oracle_execute</c>'s writes and reads alike — after the gate, before the statement or the allow pane.
/// Refused: what describes the whole database rather than the schema — the dictionary's <c>ALL_</c>, <c>DBA_</c> and <c>CDB_</c> views
/// (every schema's objects) and <c>V$</c>/<c>GV$</c> (the instance and its sessions); <c>USER_</c> stays — and a qualifier naming
/// another schema. A lexer cannot tell <c>hr.employees</c> from an alias's <c>e.id</c>, nor the view <c>ALL_USERS</c> from a column
/// <c>DBA_NAME</c> (the review of the same day: such columns were refused), so the tools read the database's names first
/// (<see cref="Names"/>: the users — every schema is one, Oracle's own among them — and SYS's public synonyms with those prefixes)
/// and refuse a qualifier among the users (an alias spelled like one too, which the refusal says to rename) or a word among the
/// views. Names compare as Oracle reads them: a bare word upper-cased, a quoted one as written. A sequence's <c>NEXTVAL</c>/<c>CURRVAL</c>
/// and a trigger's <c>:NEW.x</c> are no qualifiers; a package's <c>DBMS_LOB.SUBSTR</c> names no user and stands. Unqualified names
/// resolve in <c>CURRENT_SCHEMA</c>, the lock's schema, or through public synonyms; what those reach (a synonym, view or routine that
/// reads another schema) is the account's to guard. Pure.
/// </summary>
public static class OracleBoundary
{
    /// <summary>The prefixes of the dictionary views of the whole database and instance rather than the schema.</summary>
    public static readonly IReadOnlyList<string> DictionaryPrefixes = ["ALL_", "DBA_", "CDB_", "V$", "GV$", "V_$", "GV_$"];

    /// <summary>The words after a dot that name no object: a sequence's values.</summary>
    public static readonly IReadOnlySet<string> Members = ServerWriteGate.Words("NEXTVAL", "CURRVAL");

    /// <summary>The kind <see cref="Names"/> gives a user's (schema's) name.</summary>
    public const string UserKind = "U";

    /// <summary>The kind <see cref="Names"/> gives a dictionary view's.</summary>
    public const string ViewKind = "D";

    /// <summary>
    /// The names read before the model's statement: <c>kind, name</c> — every user (<see cref="UserKind"/>), and SYS's public synonyms
    /// with a dictionary prefix (<see cref="ViewKind"/>: <c>ALL_TABLES</c>, <c>V$SESSION</c>…).
    /// </summary>
    public const string Names =
        """
        SELECT 'U' AS kind, username AS name FROM sys.all_users
        UNION ALL
        SELECT 'D', synonym_name FROM sys.all_synonyms
        WHERE owner = 'PUBLIC' AND table_owner = 'SYS'
          AND (synonym_name LIKE 'ALL\_%' ESCAPE '\' OR synonym_name LIKE 'DBA\_%' ESCAPE '\' OR synonym_name LIKE 'CDB\_%' ESCAPE '\'
               OR synonym_name LIKE 'V$%' OR synonym_name LIKE 'GV$%')
        """;

    /// <summary>How many names <see cref="Names"/> reads at most.</summary>
    public const int MaxNames = 200_000;

    /// <summary>
    /// What of <paramref name="sql"/> the database must be asked about: its qualifiers but the schema <paramref name="place"/>, and its
    /// words with a dictionary prefix that the schema does not qualify — none of either for most statements.
    /// </summary>
    public static (IReadOnlyList<string> Heads, IReadOnlyList<string> Dictionary) Qualifiers(string sql, string place)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(place);
        if (OracleWriteGate.Lex(sql, out _) is not { } tokens)
        {
            return ([], []);
        }

        var dictionary = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            bool ownQualifier = i >= 2 && tokens[i - 1].IsSymbol(".") && tokens[i - 2].Text.Equals(place, StringComparison.Ordinal);
            if (t.Kind is GateTokenKind.Word or GateTokenKind.Name && !ownQualifier
                && DictionaryPrefixes.Any(p => t.Text.StartsWith(p, StringComparison.Ordinal)))
            {
                dictionary.Add(t.Text);
            }
        }

        // The lexer upper-cases a bare word, as Oracle folds it; a quoted name keeps its case: both compare exactly (the review,
        // 2026-10-09: "Hr" is not HR).
        var heads = ServerWriteGate.Qualifiers(tokens, Members).Where(h => !h.Equals(place, StringComparison.Ordinal)).ToList();
        return (heads, dictionary);
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to the schema <paramref name="place"/>, the database's names being <paramref name="names"/>
    /// (<see cref="Names"/>' rows); else the <c>Error:</c> sentence. A text that does not lex passes: the gate refused it.
    /// </summary>
    public static string? Check(string sql, string place, IEnumerable<string[]> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var rows = names.ToList();
        return Check(sql, place, rows.Where(r => r[0] == UserKind).Select(r => r[1]), rows.Where(r => r[0] == ViewKind).Select(r => r[1]));
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to the schema <paramref name="place"/>, the users in the database being
    /// <paramref name="users"/> and its dictionary views <paramref name="views"/>; else the <c>Error:</c> sentence.
    /// </summary>
    public static string? Check(string sql, string place, IEnumerable<string> users, IEnumerable<string> views)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(views);
        var (heads, dictionary) = Qualifiers(sql, place);
        var known = new HashSet<string>(views, StringComparer.Ordinal);
        if (dictionary.FirstOrDefault(known.Contains) is { } view)
        {
            return OracleText.Dictionary(view, place);
        }

        var others = new HashSet<string>(users, StringComparer.Ordinal);
        return heads.FirstOrDefault(others.Contains) is { } head ? OracleText.OtherSchema(head, place) : null;
    }
}
