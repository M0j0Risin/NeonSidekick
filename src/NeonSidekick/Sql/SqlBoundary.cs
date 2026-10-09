using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace NeonSidekick.Sql;

/// <summary>
/// The lock that holds a SQL connection to its one database (2026-10-09, the user's call: "I just want to be able to use a connection
/// and know it's locked down to only the database I configured it for"; another database is another connection). Checked on the
/// model's own text — <c>sql_query</c>, and <c>sql_execute</c>'s writes and reads alike — after the family's gate, before anything
/// reaches the server; the app's catalog batches are its own and never pass here. Parsed by ScriptDom, as the gates are, so a
/// database's part of a name is told from a schema's (<c>dbo.t</c> stays inside) and an alias's (<c>t.col</c>). Refused:
/// <list type="bullet">
/// <item>a name in another database — a three-part object name (<c>payroll.dbo.salaries</c>, <c>master.sys.objects</c>), a function
/// called as <c>db.schema.f()</c>, a four-part column, an <c>OBJECT_ID</c> of a three-part literal (<see cref="NamedDatabase"/>);</item>
/// <item>what describes the whole server rather than the database (the user's pick the same day: names of other databases, logins,
/// other sessions' query text): the server-scoped catalog views (<see cref="ServerViews"/>, <see cref="ServerViewPrefixes"/>,
/// every <c>sys.dm_*</c>), the old compatibility views (<c>sysdatabases</c>, <c>sysprocesses</c>…), the <c>fn_</c> functions that read
/// the server's files and logs, and the functions that look a database up by name or id (<see cref="DatabaseFunctions"/>).</item>
/// </list>
/// What the text cannot show is the login's to guard: a view, synonym, procedure or function inside the database that reads another
/// one, a name built in a string. A text that does not parse passes here: the gate before this has already refused it. Pure.
/// </summary>
public static class SqlBoundary
{
    /// <summary>The <c>sys</c> catalog views of the server rather than the database. Case-blind.</summary>
    public static readonly IReadOnlySet<string> ServerViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "databases", "master_files", "sql_logins", "servers", "linked_logins", "remote_logins", "credentials", "endpoints",
        "configurations", "traces", "backup_devices", "login_token", "database_recovery_status",
    };

    /// <summary>The prefixes of the server's <c>sys</c> views: the dynamic management views and functions, the server's principals, permissions, triggers and audits, availability groups, mirroring.</summary>
    public static readonly IReadOnlyList<string> ServerViewPrefixes = ["dm_", "server_", "availability_", "database_mirroring"];

    /// <summary>The compatibility views (any schema, or none: they resolve unqualified) and the <c>fn_</c> functions over the server's files and logs. Case-blind.</summary>
    public static readonly IReadOnlySet<string> ServerObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sysdatabases", "syslogins", "sysprocesses", "sysservers", "sysaltfiles", "sysdevices", "sysremotelogins", "sysoledbusers",
        "syslockinfo", "sysperfinfo", "sysconfigures", "syscurconfigs", "syscacheobjects",
        "fn_get_audit_file", "fn_xe_file_target_read_file", "fn_trace_gettable", "fn_trace_getinfo", "fn_dblog", "fn_dump_dblog",
        "fn_virtualfilestats", "fn_servershareddrives",
    };

    /// <summary>
    /// The functions that name or test a database by its name or id. Their first argument may be this one: <c>DB_NAME()</c> or
    /// <c>DB_ID()</c> with none, or the database's own name as a literal (the review, 2026-10-09: <c>DATABASEPROPERTYEX(DB_NAME(), …)</c>
    /// was refused). Any other is refused.
    /// </summary>
    public static readonly IReadOnlySet<string> DatabaseFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DB_NAME", "DB_ID", "HAS_DBACCESS", "DATABASEPROPERTYEX", "DATABASEPROPERTY",
    };

    /// <summary>
    /// The functions that look up the server's logins or another database's objects, refused given an argument (the review, 2026-10-09):
    /// <c>SUSER_NAME(n)</c> walks the logins; <c>SUSER_SNAME()</c> with none (the caller's own) passes.
    /// </summary>
    public static readonly IReadOnlySet<string> LoginFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SUSER_NAME", "SUSER_SNAME", "SUSER_ID", "SUSER_SID", "LOGINPROPERTY", "HAS_PERMS_BY_NAME",
    };

    /// <summary>The functions whose second argument is a database's id (<c>OBJECT_NAME(id, dbid)</c>), or whose second names another login (<c>IS_SRVROLEMEMBER</c>).</summary>
    public static readonly IReadOnlySet<string> SecondArgumentFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "OBJECT_NAME", "OBJECT_SCHEMA_NAME", "IS_SRVROLEMEMBER",
    };

    /// <summary>
    /// What <see cref="Check(string, string, IEnumerable{string}?)"/> finds in <paramref name="sql"/> without a server: the refusal, or
    /// the heads of the calls that may be another database's function or a table alias's column method (<c>payroll.dbo.f()</c> beside an
    /// alias <c>payroll</c> reads the same as <c>a.Shape.STAsText()</c> — the review, 2026-10-09), which only the server's database
    /// names can settle.
    /// </summary>
    public static (string? Refused, IReadOnlyList<string> Ambiguous) Inspect(string sql, string database)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(database);
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out errors);
        }

        if (errors.Count > 0)
        {
            return (null, []);
        }

        var tables = new TableNames();
        fragment.Accept(tables);
        var outside = new Outside(database.Trim(), tables.Names);
        fragment.Accept(outside);
        return (outside.Found, outside.Ambiguous);
    }

    /// <summary>
    /// Null when <paramref name="sql"/> keeps to <paramref name="database"/>; else the <c>Error:</c> sentence. <paramref name="databases"/>
    /// are the server's database names, which settle a call that may be another database's (<see cref="Inspect"/>); without them such
    /// a call passes, so the tools read them whenever there is one.
    /// </summary>
    public static string? Check(string sql, string database, IEnumerable<string>? databases = null)
    {
        var (refused, ambiguous) = Inspect(sql, database);
        if (refused is not null || ambiguous.Count == 0 || databases is null)
        {
            return refused;
        }

        var names = new HashSet<string>(databases, StringComparer.OrdinalIgnoreCase);
        return ambiguous.FirstOrDefault(a => names.Contains(a) && IsOther(a, database)) is { } named ? SqlText.AliasLikeDatabase(named, database.Trim()) : null;
    }

    /// <summary>
    /// The database part of an object name written as text (<c>payroll.dbo.salaries</c>, <c>[pay roll]..t</c>, <c>"x".y.z</c>): the
    /// first of three or four parts, its brackets or quotes taken off; null for a one- or two-part name. What <c>sql_describe</c> and
    /// the rest are handed as <c>table</c>, and an <c>OBJECT_ID</c> literal, go through it.
    /// </summary>
    public static string? NamedDatabase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        char close = '\0';
        foreach (char c in name.Trim())
        {
            if (close != '\0')
            {
                if (c == close)
                {
                    close = '\0';
                }
                else
                {
                    part.Append(c);
                }
            }
            else if (c is '[' or '"')
            {
                close = c == '[' ? ']' : '"';
            }
            else if (c == '.')
            {
                parts.Add(part.ToString());
                part.Clear();
            }
            else
            {
                part.Append(c);
            }
        }

        parts.Add(part.ToString());
        return parts.Count >= 3 ? parts[^3].Trim() : null;
    }

    /// <summary>Whether <paramref name="named"/> (a database part, or null for none) is another database than <paramref name="database"/>; <c>a..t</c>'s empty part is this one.</summary>
    public static bool IsOther(string? named, string database) =>
        named is { Length: > 0 } && !named.Equals(database.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The names a two-part call target may lead with as a column's table: every table's name and alias in the statement.</summary>
    private sealed class TableNames : TSqlFragmentVisitor
    {
        public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject?.BaseIdentifier?.Value is { } name)
            {
                Names.Add(name);
            }

            if (node.Alias?.Value is { } alias)
            {
                Names.Add(alias);
            }

            base.Visit(node);
        }

        public override void Visit(TableReferenceWithAlias node)
        {
            if (node.Alias?.Value is { } alias)
            {
                Names.Add(alias);
            }

            base.Visit(node);
        }

        public override void Visit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is { } name)
            {
                Names.Add(name);
            }

            base.Visit(node);
        }
    }

    /// <summary>The first name in the statement outside the database, as its refusal.</summary>
    private sealed class Outside(string database, HashSet<string> tables) : TSqlFragmentVisitor
    {
        public string? Found { get; private set; }

        public List<string> Ambiguous { get; } = [];

        /// <summary>Whether a function's first argument is this database: none, <c>DB_NAME()</c>/<c>DB_ID()</c> with none, or its own name as a literal.</summary>
        private bool ThisDatabase(FunctionCall node) => node.Parameters.Count == 0 || node.Parameters[0] switch
        {
            FunctionCall { CallTarget: null, Parameters.Count: 0 } inner => inner.FunctionName?.Value is { } f && (f.Equals("DB_NAME", StringComparison.OrdinalIgnoreCase) || f.Equals("DB_ID", StringComparison.OrdinalIgnoreCase)),
            StringLiteral literal => !IsOther(literal.Value, database),
            _ => false,
        };

        private void OtherDatabase(string? named)
        {
            if (Found is null && IsOther(named, database))
            {
                Found = SqlText.OtherDatabase(named!, database);
            }
        }

        private void Server(string what) => Found ??= SqlText.ServerCatalog(what);

        public override void Visit(SchemaObjectName node)
        {
            OtherDatabase(node.DatabaseIdentifier?.Value);
            string? schema = node.SchemaIdentifier?.Value;
            if (node.BaseIdentifier?.Value is { } name)
            {
                if (ServerObjects.Contains(name))
                {
                    Server(name);
                }
                else if (string.Equals(schema, "sys", StringComparison.OrdinalIgnoreCase)
                    && (ServerViews.Contains(name) || ServerViewPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                {
                    Server("sys." + name);
                }
            }

            base.Visit(node);
        }

        public override void Visit(FunctionCall node)
        {
            string name = node.FunctionName?.Value ?? "";
            switch (node.CallTarget)
            {
                // db.schema.f(): a database's function; t.col.Method() leads with a table's name or alias (a CLR type's method). A head that
                // is both is settled by the server's database names (the review, 2026-10-09: the alias exemption alone let payroll.dbo.f() by).
                case MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: >= 2 } parts } when tables.Contains(parts[0].Value):
                    if (IsOther(parts[0].Value, database))
                    {
                        Ambiguous.Add(parts[0].Value);
                    }

                    break;
                case MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers: { Count: >= 2 } parts }:
                    OtherDatabase(parts[0].Value);
                    break;
                case null when DatabaseFunctions.Contains(name) && !ThisDatabase(node):
                    Server(name.ToUpperInvariant() + "()");
                    break;
                case null when LoginFunctions.Contains(name) && node.Parameters.Count > 0:
                    Server(name.ToUpperInvariant() + "()");
                    break;
                case null when SecondArgumentFunctions.Contains(name) && node.Parameters.Count > 1:
                    Server(name.ToUpperInvariant() + "()");
                    break;
                case null when ServerObjects.Contains(name):
                    Server(name);
                    break;
                case null when name.Equals("OBJECT_ID", StringComparison.OrdinalIgnoreCase) && node.Parameters is [StringLiteral literal, ..]:
                    OtherDatabase(NamedDatabase(literal.Value));
                    break;
            }

            base.Visit(node);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is { Identifiers.Count: >= 4 } parts)
            {
                OtherDatabase(parts.Identifiers[^4].Value);
            }

            base.Visit(node);
        }
    }
}
