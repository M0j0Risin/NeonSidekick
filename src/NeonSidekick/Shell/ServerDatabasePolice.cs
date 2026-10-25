using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>
/// What the server-database police looks for (2026-10-05): each family whose tools are on, by its title as the 👮 line names it, the
/// words that reach it from a shell or a script (its clients, its drivers), and the hosts its connections name — a loopback host only
/// with its port (<c>localhost:5432</c>, <c>localhost,1433</c>), anything else as a whole name.
/// </summary>
public sealed record ServerDatabaseGuard(IReadOnlyList<ServerDatabaseGuard.Family> Families)
{
    /// <summary>One family: its title (<c>PostgreSQL</c>), its read and change tools for the model's sentence, its words, its hosts as matched.</summary>
    public sealed record Family(string Title, string QueryTool, string ExecuteTool, IReadOnlyList<string> Words, IReadOnlyList<string> Hosts);
}

/// <summary>
/// The shell police's server-database rule (2026-10-05, the user's call: SQLite's rule, <see cref="SqlitePolice"/>, mirrored for SQL
/// Server, Oracle, MySQL and PostgreSQL — the clients, the drivers and the configured hosts): while a family's tools are on and
/// <c>Shell police</c> is, a <c>run_command</c> line (and the script files it runs), an <c>execute_code</c> script or a <c>process</c>
/// write that reaches that family is refused before the gate is asked, whatever the policy, in either mode — so its databases are
/// reached only through its tools, whose gates, mode, kinds, access and allow pane then hold. A word is found case-blind inside a
/// longer name (<c>pymysql</c>, <c>Microsoft.Data.SqlClient</c>, <c>psycopg2</c>) unless it is short enough to be part of anything
/// (<see cref="WholeWords"/>: <c>bcp</c>, <c>psql</c>, <c>osql</c>, <c>rman</c>, <c>sqlcl</c>), which counts only standing alone. A
/// tripwire like <see cref="ForbiddenStrings"/>, not a sandbox: text built in pieces gets past it; only the <c>ask</c> policy shows
/// the user every command. A package-manager command that only installs, removes, restores or lists packages is not judged
/// (2026-10-10, the user's call, <see cref="PackageCommands"/>). Pure but for <see cref="For"/>'s reads of the connections files and
/// <see cref="Judge"/>'s of scripts.
/// </summary>
public static partial class ServerDatabasePolice
{
    /// <summary>SQL Server's clients and drivers.</summary>
    /// <remarks>
    /// Since 2026-10-24 (the database review's A14, G12): Node's <c>tedious</c>, the ODBC and OLE DB routes (the driver names a connection
    /// string spells, <c>System.Data.Odbc</c>/<c>OleDb</c>, which reach any server a DSN names), dbatools, and JDBC's URL.
    /// </remarks>
    public static readonly IReadOnlyList<string> SqlServerWords =
    [
        "sqlcmd", "osql", "bcp", "sqlpackage", "Invoke-Sqlcmd", "SqlServer", "SqlClient", "pyodbc", "pymssql", "mssql",
        "tedious", "{SQL Server", "Driver for SQL Server", "MSOLEDBSQL", "SQLOLEDB", "SQLNCLI", "System.Data.Odbc", "System.Data.OleDb",
        "Invoke-DbaQuery", "dbatools", "jdbc:sqlserver",
    ];

    /// <summary>Oracle's: since 2026-10-24 (I8, Q4, G12) SQL*Loader, the Java driver, the ODBC and OLE DB drivers' names.</summary>
    public static readonly IReadOnlyList<string> OracleWords =
    [
        "sqlplus", "sqlcl", "oracledb", "cx_Oracle", "ManagedDataAccess", "tnsping", "expdp", "impdp", "rman",
        "sqlldr", "ojdbc", "jdbc:oracle", "OracleDriver", "OraOLEDB", "Oracle in Ora", "Oracle in instantclient",
    ];

    /// <summary>MySQL's and MariaDB's (<c>mysql</c> covers mysqldump, mysqlsh, pymysql, MySqlConnector, mysql2, MySQLdb).</summary>
    public static readonly IReadOnlyList<string> MySqlWords = ["mysql", "mariadb"];

    /// <summary>
    /// PostgreSQL's (<c>postgres</c> covers postgresql); since 2026-10-24 (A14, I8) node-postgres's package <c>pg</c>, as code names it, quoted
    /// (<c>require('pg')</c>; bare it is any flag or folder: <c>gcc -pg</c>, <c>src/pg/</c>), and the server's own client programs.
    /// </summary>
    public static readonly IReadOnlyList<string> PostgresWords =
    [
        "postgres", "psql", "pg_dump", "pg_dumpall", "pg_restore", "pgcli", "psycopg", "asyncpg", "npgsql", "pg8000",
        "'pg'", "\"pg\"", "dropdb", "createdb", "vacuumdb", "clusterdb", "reindexdb", "createuser", "dropuser", "pg_basebackup", "pg_isready",
    ];

    /// <summary>The words so short they would be found inside unrelated names: they count only standing alone.</summary>
    public static readonly IReadOnlySet<string> WholeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bcp", "osql", "psql", "rman", "sqlcl", "dropdb", "createdb", "vacuumdb", "clusterdb", "reindexdb", "createuser", "dropuser", "sqlldr",
    };

    /// <summary>The host names a loopback connection uses: matched only with its port, so <c>curl localhost:8080</c> is no database.</summary>
    public static readonly IReadOnlySet<string> Loopback = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", "[::1]", ".", "(local)", "host.docker.internal" };

    /// <summary>
    /// The guard for the families whose tools are on in <paramref name="effective"/>, their hosts from every connection their files
    /// name (offered or not, and skipped with a problem too — 2026-10-09: an entry from before the database was required is no
    /// connection the tools use, but its server is still one the shell must not reach; and since 2026-10-24 the home's entries a profile's
    /// shadows, review H9, read through the cached loads, B15); null when none is on.
    /// </summary>
    public static ServerDatabaseGuard? For(AppSettingsData effective, string profileDirectory, string? storageDirectory)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(profileDirectory);
        var families = new List<ServerDatabaseGuard.Family>();
        if (effective.SqlTools)
        {
            var hosts = Sql.SqlConfigFile.Family.Everything(profileDirectory, storageDirectory).SelectMany(c => SqlServerHosts(c.Server));
            families.Add(new("SQL Server", Llm.Tools.SqlQueryTool.ToolName, Llm.Tools.SqlExecuteTool.ToolName, SqlServerWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.OracleTools)
        {
            var hosts = Oracle.OracleConfigFile.Family.Everything(profileDirectory, storageDirectory).SelectMany(c => OracleHosts(c.DataSource));
            families.Add(new("Oracle", Llm.Tools.OracleQueryTool.ToolName, Llm.Tools.OracleExecuteTool.ToolName, OracleWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.MySqlTools)
        {
            var hosts = MySql.MySqlConfigFile.Family.Everything(profileDirectory, storageDirectory).SelectMany(c => Hosts(c.Host, c.Port ?? MySql.MySqlConnectionConfig.DefaultPort));
            families.Add(new("MySQL", Llm.Tools.MySqlQueryTool.ToolName, Llm.Tools.MySqlExecuteTool.ToolName, MySqlWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.PostgresTools)
        {
            var hosts = Postgres.PostgresConfigFile.Family.Everything(profileDirectory, storageDirectory).SelectMany(c => Hosts(c.Host, c.Port ?? Postgres.PostgresConnectionConfig.DefaultPort));
            families.Add(new("PostgreSQL", Llm.Tools.PostgresQueryTool.ToolName, Llm.Tools.PostgresExecuteTool.ToolName, PostgresWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return families.Count == 0 ? null : new ServerDatabaseGuard(families);
    }

    /// <summary>What a host matches as: a loopback one only with its port (<c>host:port</c> and SQL Server's <c>host,port</c>), else the host alone.</summary>
    /// <remarks>
    /// Since 2026-10-24 (the database review's D6) a host list (<c>db1,db2</c>, <c>db1:5433,db2</c>: Npgsql's and MySqlConnector's failover) is
    /// each of its hosts, each with its own port when it writes one.
    /// </remarks>
    public static IEnumerable<string> Hosts(string? host, int port)
    {
        var found = new List<string>();
        foreach (string entry in (host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = entry;
            int at = port;
            int colon = entry.LastIndexOf(':');
            if (colon > 0 && !entry.EndsWith(']') && entry.IndexOf(':') == colon && int.TryParse(entry[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int written))
            {
                (name, at) = (entry[..colon], written);
            }

            string text = at.ToString(CultureInfo.InvariantCulture);
            found.AddRange(Loopback.Contains(name) ? [name + ":" + text, name + "," + text] : [name]);
        }

        return found;
    }

    /// <summary>A SQL Server <c>server</c>'s hosts: <c>host</c>, <c>host,port</c> or <c>host\instance</c> (1433 when no port is given).</summary>
    public static IEnumerable<string> SqlServerHosts(string? server)
    {
        string text = server?.Trim() ?? "";
        // The protocol prefixes (2026-10-24, the database review's B8: only tcp: was taken off, so np:\\host\pipe\…, admin:host and lpc:host
        // gave a host no line spells); a named pipe's host is its \\host part.
        foreach (string prefix in new[] { "tcp:", "admin:", "lpc:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
            }
        }

        if (text.StartsWith("np:", StringComparison.OrdinalIgnoreCase))
        {
            string pipe = text[3..].TrimStart('\\', '/');
            int end = pipe.IndexOfAny(['\\', '/']);
            return Hosts(end < 0 ? pipe : pipe[..end], 1433);
        }

        int comma = text.IndexOf(',', StringComparison.Ordinal);
        string[] parts = (comma >= 0 ? text[..comma] : text).Split('\\');
        string host = parts[0].Trim();
        int port = comma >= 0 && int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 1433;
        var hosts = Hosts(host, port);
        // A loopback named instance is reached as host\instance, which neither host:port nor host,port spells (2026-10-22, the shell-review
        // fixes); a non-loopback host already matches as its bare name inside host\instance, so only the loopback form needs the extra spelling.
        string instance = parts.Length > 1 ? parts[1].Trim() : "";
        return instance.Length > 0 && Loopback.Contains(host) ? hosts.Append(host + "\\" + instance) : hosts;
    }

    /// <summary>An Oracle data source's hosts: EZConnect's <c>host[:port]/service</c>, or every <c>HOST=</c> of a descriptor (1521 when no port is given).</summary>
    public static IEnumerable<string> OracleHosts(string? dataSource)
    {
        string text = dataSource?.Trim() ?? "";
        if (text.StartsWith('('))
        {
            var found = new List<string>();
            var ports = DescriptorPort().Matches(text);
            foreach (Match m in DescriptorHost().Matches(text))
            {
                // The PORT nearest this HOST, before or after it (2026-10-22, the shell-review fixes): a descriptor may write PORT before HOST,
                // or omit it, and pairing with the first PORT at or after the HOST recorded the wrong (or the default) port.
                int port = 1521;
                int best = int.MaxValue;
                foreach (Match pm in ports)
                {
                    int distance = Math.Abs(pm.Index - m.Index);
                    // TryParse (2026-10-24, review B7: int.Parse threw on a long number, and every police check with it).
                    if (distance < best && int.TryParse(pm.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    {
                        best = distance;
                        port = parsed;
                    }
                }

                found.AddRange(Hosts(m.Groups[1].Value, port));
            }

            return found;
        }

        // EZConnect plus (2026-10-24, review D6): a tcp:// or tcps:// scheme, and a host list h1,h2:1521 — each host with the port written last.
        foreach (string scheme in new[] { "tcps://", "tcp://" })
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text[scheme.Length..];
            }
        }

        string hostPort = text.StartsWith("//", StringComparison.Ordinal) ? text[2..] : text;
        hostPort = hostPort.Split('/')[0].Split('?')[0];
        int colon = hostPort.LastIndexOf(':');
        bool ported = colon > 0 && int.TryParse(hostPort[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        int listener = ported ? int.Parse(hostPort[(colon + 1)..], CultureInfo.InvariantCulture) : 1521;
        return Hosts(ported ? hostPort[..colon] : hostPort, listener);
    }

    /// <summary>What in <paramref name="text"/> reaches a family of <paramref name="guard"/>: the family's title and the token as written; else null.</summary>
    public static (string Family, string Token)? Find(string text, ServerDatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        foreach (var family in guard.Families)
        {
            foreach (string word in family.Words)
            {
                if (FindWord(text, word, WholeWords.Contains(word)) is { } token)
                {
                    return (family.Title, token);
                }
            }

            foreach (string host in family.Hosts)
            {
                if (FindHost(text, host))
                {
                    return (family.Title, host);
                }
            }
        }

        return null;
    }

    /// <summary>The family that <paramref name="title"/> names in <paramref name="guard"/>.</summary>
    public static ServerDatabaseGuard.Family FamilyOf(ServerDatabaseGuard guard, string title)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return guard.Families.First(f => f.Title == title);
    }

    /// <summary>
    /// The verdict on a <c>run_command</c> line: what in it reaches a family, else what in one of its script files does
    /// (<see cref="SqlitePolice.ScriptFiles"/>, <c>File</c> naming it); null when neither does.
    /// </summary>
    public static (string Family, string Token, string? File)? Judge(string command, string workdir, string root, ServerDatabaseGuard guard) =>
        Judge(command, new Lazy<IReadOnlyList<(string Name, string Text)>>(() => SqlitePolice.ScriptFiles(command, workdir, root)), guard);

    /// <summary><see cref="Judge(string, string, string, ServerDatabaseGuard)"/> over script files read at most once (2026-10-12, the SQLite police's reason: <see cref="SqlitePolice.ScriptFiles"/> read once for both).</summary>
    public static (string Family, string Token, string? File)? Judge(string command, Lazy<IReadOnlyList<(string Name, string Text)>> scripts, ServerDatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(guard);
        // The line less its package-manager segments (2026-10-10, PackageCommands: dotnet add package Npgsql reaches no database), its lookups
        // and a commit's message (2026-10-24, review Q5: grep mysql and git commit -m "mysql fix" reach no database).
        if (Find(SqlitePolice.Judged(command), guard) is { } found)
        {
            return (found.Family, found.Token, null);
        }

        foreach (var (name, text) in scripts.Value)
        {
            if (Find(text, guard) is { } inScript)
            {
                return (inScript.Family, inScript.Token, name);
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="word"/> in <paramref name="text"/>, case-blind, with the name around it as written (<c>pymysql</c>); a
    /// <paramref name="whole"/> word only standing alone (no letter, digit or <c>_</c> beside it).
    /// </summary>
    private static string? FindWord(string text, string word, bool whole)
    {
        int from = 0;
        while ((from = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int start = from, end = from + word.Length;
            if (whole)
            {
                bool alone = (start == 0 || !IsWordChar(text[start - 1])) && (end == text.Length || !IsWordChar(text[end]));
                if (alone)
                {
                    return text[start..end];
                }

                from = end;
                continue;
            }

            while (start > 0 && IsNameChar(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && IsNameChar(text[end]))
            {
                end++;
            }

            return text[start..end];
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="host"/> stands in <paramref name="text"/> as a whole name: no name character beside it. Since 2026-10-24 (the
    /// database review's Q6) a one-label name (<c>db</c>, a TNS alias <c>test</c>) counts only where a host stands — after <c>@</c>, <c>//</c>,
    /// <c>=</c> or a host option (<c>-h</c>, <c>-S</c>, <c>--host</c>), or before a port — so <c>cd db</c> and <c>npm test</c> are no database;
    /// and a loopback <c>host:port</c> is found with the host and the port written apart too (A14: <c>-h localhost -p 5432</c>).
    /// </summary>
    private static bool FindHost(string text, string host)
    {
        if (LoopbackPort(host) is { } parts)
        {
            return FindPlain(text, host) || (FindWhole(text, parts.Host) && FindWhole(text, parts.Port));
        }

        return !host.Contains('.') && !host.Contains(':') && !host.All(char.IsAsciiDigit) ? FindPlacedHost(text, host) : FindPlain(text, host);
    }

    /// <summary>A loopback entry's host and port (<c>localhost:5432</c>, <c>localhost,5432</c>); null for any other host.</summary>
    private static (string Host, string Port)? LoopbackPort(string host)
    {
        int split = host.LastIndexOfAny([':', ',']);
        return split > 0 && Loopback.Contains(host[..split]) && host[(split + 1)..].All(char.IsAsciiDigit) ? (host[..split], host[(split + 1)..]) : null;
    }

    /// <summary>Whether <paramref name="word"/> stands alone in <paramref name="text"/>: no letter, digit, <c>_</c>, <c>.</c> or <c>-</c> beside it.</summary>
    private static bool FindWhole(string text, string word)
    {
        int from = 0;
        while (word.Length > 0 && (from = text.IndexOf(word, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = from + word.Length;
            if ((from == 0 || !IsHostChar(text[from - 1])) && (end == text.Length || !IsHostChar(text[end])))
            {
                return true;
            }

            from = end;
        }

        return false;
    }

    /// <summary>A one-label host where a host stands (<see cref="FindHost"/>).</summary>
    private static bool FindPlacedHost(string text, string host) =>
        Regex.IsMatch(text, @"(?:(?:@|//|=)\s*[""']?|(?:^|\s)(?:-h|-H|-S|--host|--server|-Server|-ServerInstance|/S)[\s=:]+[""']?)" + Regex.Escape(host) + @"(?![\w.\-])"
            + "|" + @"(?<![\w.\-])" + Regex.Escape(host) + @"[:,]\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="host"/> stands in <paramref name="text"/> as a whole name: no name character beside it.</summary>
    private static bool FindPlain(string text, string host)
    {
        int from = 0;
        while (host.Length > 0 && (from = text.IndexOf(host, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = from + host.Length;
            bool before = from == 0 || !IsHostChar(text[from - 1]);
            bool after = end == text.Length || !IsHostChar(text[end]) || (text[end] == '.' && (end + 1 == text.Length || !IsHostChar(text[end + 1])));
            if (before && after)
            {
                return true;
            }

            from = end;
        }

        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or ':';

    private static bool IsHostChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';

    [GeneratedRegex(@"HOST\s*=\s*([^)\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DescriptorHost();

    [GeneratedRegex(@"PORT\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DescriptorPort();
}
