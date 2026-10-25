using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// The lock that holds a MySQL connection to its one database (2026-10-09, the user's call, SQL Server's <see cref="SqlBoundary"/>
/// carried over): checked on the model's own text — <c>mysql_query</c>, and <c>mysql_execute</c>'s writes and reads alike — after the
/// gate, before the statement or the allow pane. A MySQL database is its schema, so another is reached by qualifying a name
/// (<c>payroll.salaries</c>, <c>`payroll`.salaries</c>, <c>payroll.salaries.id</c>, <c>payroll.f()</c>): <c>USE</c> is refused by both
/// gates, and the session never changes its database. A lexer cannot tell a qualifier from a table's alias (<c>t.col</c>), so the
/// tools read the names of the databases on the server first (<see cref="Names"/>, what the account can see — what it cannot see it
/// cannot reach) and a qualifier among them is refused (<see cref="Check"/>): another database, or an alias spelled
/// like one, which the refusal says to rename. The server's own databases (<see cref="SystemDatabases"/>: <c>information_schema</c>'s
/// listing of every database's tables, <c>performance_schema</c>'s other sessions' statements, <c>mysql</c>'s accounts) are refused
/// whether listed or not, the user's pick the same day: the catalog tools read the connection's own. What the text cannot show (a view
/// or routine in the database that reads another) is the account's to guard. Pure.
/// </summary>
public static class MySqlBoundary
{
    /// <summary>The server's own databases: refused as a qualifier whatever the account sees.</summary>
    public static readonly IReadOnlySet<string> SystemDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema", "mysql", "performance_schema", "sys",
    };

    /// <summary>The names of the databases the account can see, read before the model's statement.</summary>
    public const string Names = "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA";

    /// <summary>How many names <see cref="Names"/> reads at most: every one on any server a person keeps.</summary>
    public const int MaxNames = 100_000;

    /// <summary>
    /// What of <paramref name="sql"/> the server must be asked about: its qualifiers — none for most statements, which then need no
    /// names read — or, when one is the server's own, the refusal at once. The connection's own database written in another case stays
    /// one to ask about (the review, 2026-10-09): the lexer upper-cases a bare word, and on a server whose names are case-sensitive
    /// <c>Shop</c> and <c>shop</c> are two databases, which only the server's names tell apart. Only a quoted name spelled exactly as
    /// the connection's own is its own.
    /// </summary>
    public static (IReadOnlyList<string> Heads, string? Refused) Qualifiers(string sql, string database)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(database);
        if (MySqlWriteGate.Lex(sql, out _) is not { } tokens)
        {
            return ([], MySqlText.BoundaryUnread);   // refused, not passed (2026-10-24, mechanism 7)
        }

        string own = database.Trim();
        var heads = Others(tokens, own).ToList();
        return heads.FirstOrDefault(SystemDatabases.Contains) is { } system
            ? (heads, MySqlText.ServerCatalog(system.ToLowerInvariant()))
            : (heads, null);
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to <paramref name="database"/>, the other databases on the server being
    /// <paramref name="visible"/>; else the <c>Error:</c> sentence. A text that does not lex is refused (2026-10-24, mechanism 7).
    /// </summary>
    public static string? Check(string sql, string database, IEnumerable<string> visible)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(visible);
        if (MySqlWriteGate.Lex(sql, out _) is not { } tokens)
        {
            return MySqlText.BoundaryUnread;
        }

        // The connection's own as the server spells it, then every other name a qualifier could mean, whatever its case: a twin that
        // differs from the own only in case (a case-sensitive server's Shop beside shop) is another database, and is refused.
        string own = database.Trim();
        var names = visible.ToList();
        string cellOwn = SqlText.Cell(own);   // as the names list writes it: a cell, scrubbed, NULL quoted (2026-10-24, the review of these fixes)
        string spelled = names.FirstOrDefault(v => v.Equals(cellOwn, StringComparison.Ordinal)) ?? names.FirstOrDefault(v => v.Equals(cellOwn, StringComparison.OrdinalIgnoreCase)) ?? cellOwn;
        foreach (string written in Others(tokens, own))
        {
            string head = SqlText.Cell(written);
            if (SystemDatabases.Contains(written))
            {
                return MySqlText.ServerCatalog(written.ToLowerInvariant());
            }

            if (names.FirstOrDefault(v => v.Equals(head, StringComparison.OrdinalIgnoreCase) && !v.Equals(spelled, StringComparison.Ordinal)) is { } named)
            {
                return MySqlText.OtherDatabase(named, own);
            }
        }

        return null;
    }

    /// <summary>
    /// The qualifiers that may name another database: all but a quoted one spelled exactly as the connection's own (2026-10-24, the
    /// database review's R8: the lexer upper-cases a bare word, so with an own <c>SHOP</c> a bare <c>shop.t</c> — the database <c>shop</c> on a
    /// case-sensitive server — compared equal and was never checked). A bare word is left to <see cref="Check"/>'s names, which pass it
    /// when the only database it can mean is the connection's.
    /// </summary>
    private static IEnumerable<string> Others(IReadOnlyList<GateToken> tokens, string own) =>
        ServerWriteGate.QualifierTokens(tokens).Where(t => !(t.Kind == GateTokenKind.Name && t.Text.Equals(own, StringComparison.Ordinal))).Select(t => t.Text);
}
