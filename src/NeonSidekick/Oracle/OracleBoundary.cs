using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// The lock that holds an Oracle connection to its one schema (2026-10-09, the user's pick: the configured <c>schema</c>, else the
/// account's own — SQL Server's <see cref="SqlBoundary"/> carried over to Oracle's schemas). Checked on the model's own text —
/// <c>oracle_query</c>, and <c>oracle_execute</c>'s writes and reads alike — after the gate, before the statement or the allow pane.
/// Refused: what describes the whole database rather than the schema (<see cref="DictionaryPrefixes"/>: the <c>ALL_</c>, <c>DBA_</c>
/// and <c>CDB_</c> views list every schema's objects, <c>V$</c>/<c>GV$</c> the instance and its sessions; <c>USER_</c> stays) unless
/// the schema itself qualifies it (a table of its own named <c>ALL_ORDERS</c> is written <c>HR.ALL_ORDERS</c>); and a qualifier naming
/// another schema. A lexer cannot tell <c>hr.employees</c> from an alias's <c>e.id</c>, so the tools read the users' names first
/// (<see cref="Names"/>: every schema is a user, Oracle's own among them) and a qualifier among them is refused — an alias spelled like
/// one too, which the refusal says to rename. A sequence's <c>NEXTVAL</c>/<c>CURRVAL</c> and a trigger's <c>:NEW.x</c> are no
/// qualifiers; a package's <c>DBMS_LOB.SUBSTR</c> names no user and stands. Unqualified names resolve in <c>CURRENT_SCHEMA</c>, the
/// lock's schema, or through public synonyms; what those reach (a synonym, view or routine that reads another schema) is the
/// account's to guard. Pure.
/// </summary>
public static class OracleBoundary
{
    /// <summary>The dictionary views of the whole database and instance rather than the schema.</summary>
    public static readonly IReadOnlyList<string> DictionaryPrefixes = ["ALL_", "DBA_", "CDB_", "V$", "GV$", "V_$", "GV_$"];

    /// <summary>The words after a dot that name no object: a sequence's values.</summary>
    public static readonly IReadOnlySet<string> Members = ServerWriteGate.Words("NEXTVAL", "CURRVAL");

    /// <summary>The users' (schemas') names, read before the model's statement.</summary>
    public const string Names = "SELECT username FROM sys.all_users";

    /// <summary>How many names <see cref="Names"/> reads at most.</summary>
    public const int MaxNames = 100_000;

    /// <summary>
    /// What of <paramref name="sql"/> the database must be asked about: its qualifiers but the schema <paramref name="place"/> — none for
    /// most statements — or, for a dictionary view the schema does not qualify, the refusal at once.
    /// </summary>
    public static (IReadOnlyList<string> Heads, string? Refused) Qualifiers(string sql, string place)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(place);
        if (OracleWriteGate.Lex(sql, out _) is not { } tokens)
        {
            return ([], null);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            bool ownQualifier = i >= 2 && tokens[i - 1].IsSymbol(".") && tokens[i - 2].Text.Equals(place, StringComparison.OrdinalIgnoreCase);
            if (t.Kind is GateTokenKind.Word or GateTokenKind.Name && !ownQualifier
                && DictionaryPrefixes.Any(p => t.Text.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return ([], OracleText.Dictionary(t.Text.ToUpperInvariant(), place));
            }
        }

        var heads = ServerWriteGate.Qualifiers(tokens, Members).Where(h => !h.Equals(place, StringComparison.OrdinalIgnoreCase)).ToList();
        return (heads, null);
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to the schema <paramref name="place"/>, the users in the database being
    /// <paramref name="visible"/>; else the <c>Error:</c> sentence. A text that does not lex passes: the gate refused it.
    /// </summary>
    public static string? Check(string sql, string place, IEnumerable<string> visible)
    {
        ArgumentNullException.ThrowIfNull(visible);
        var (heads, refused) = Qualifiers(sql, place);
        if (refused is not null || heads.Count == 0)
        {
            return refused;
        }

        var others = new HashSet<string>(visible, StringComparer.OrdinalIgnoreCase);
        if (heads.FirstOrDefault(others.Contains) is not { } head)
        {
            return null;
        }

        others.TryGetValue(head, out var named);
        return OracleText.OtherSchema(named ?? head, place);
    }
}
