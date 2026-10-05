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
/// the user every command. Pure but for <see cref="For"/>'s reads of the connections files and <see cref="Judge"/>'s of scripts.
/// </summary>
public static partial class ServerDatabasePolice
{
    /// <summary>SQL Server's clients and drivers.</summary>
    public static readonly IReadOnlyList<string> SqlServerWords = ["sqlcmd", "osql", "bcp", "sqlpackage", "Invoke-Sqlcmd", "SqlServer", "SqlClient", "pyodbc", "pymssql", "mssql"];

    /// <summary>Oracle's.</summary>
    public static readonly IReadOnlyList<string> OracleWords = ["sqlplus", "sqlcl", "oracledb", "cx_Oracle", "ManagedDataAccess", "tnsping", "expdp", "impdp", "rman"];

    /// <summary>MySQL's and MariaDB's (<c>mysql</c> covers mysqldump, mysqlsh, pymysql, MySqlConnector, mysql2, MySQLdb).</summary>
    public static readonly IReadOnlyList<string> MySqlWords = ["mysql", "mariadb"];

    /// <summary>PostgreSQL's (<c>postgres</c> covers postgresql and node-postgres).</summary>
    public static readonly IReadOnlyList<string> PostgresWords = ["postgres", "psql", "pg_dump", "pg_dumpall", "pg_restore", "pgcli", "psycopg", "asyncpg", "npgsql", "pg8000"];

    /// <summary>The words so short they would be found inside unrelated names: they count only standing alone.</summary>
    public static readonly IReadOnlySet<string> WholeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bcp", "osql", "psql", "rman", "sqlcl" };

    /// <summary>The host names a loopback connection uses: matched only with its port, so <c>curl localhost:8080</c> is no database.</summary>
    public static readonly IReadOnlySet<string> Loopback = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", "[::1]", ".", "(local)", "host.docker.internal" };

    /// <summary>
    /// The guard for the families whose tools are on in <paramref name="effective"/>, their hosts from every connection their files
    /// name (offered or not); null when none is on.
    /// </summary>
    public static ServerDatabaseGuard? For(AppSettingsData effective, string profileDirectory, string? storageDirectory)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(profileDirectory);
        var families = new List<ServerDatabaseGuard.Family>();
        if (effective.SqlTools)
        {
            var hosts = Sql.SqlConfigFile.LoadCatalog(profileDirectory, storageDirectory).Connections.SelectMany(c => SqlServerHosts(c.Config.Server));
            families.Add(new("SQL Server", Llm.Tools.SqlQueryTool.ToolName, Llm.Tools.SqlExecuteTool.ToolName, SqlServerWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.OracleTools)
        {
            var hosts = Oracle.OracleConfigFile.LoadCatalog(profileDirectory, storageDirectory).Connections.SelectMany(c => OracleHosts(c.Config.DataSource));
            families.Add(new("Oracle", Llm.Tools.OracleQueryTool.ToolName, Llm.Tools.OracleExecuteTool.ToolName, OracleWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.MySqlTools)
        {
            var hosts = MySql.MySqlConfigFile.LoadCatalog(profileDirectory, storageDirectory).Connections.SelectMany(c => Hosts(c.Config.Host, c.Config.Port ?? MySql.MySqlConnectionConfig.DefaultPort));
            families.Add(new("MySQL", Llm.Tools.MySqlQueryTool.ToolName, Llm.Tools.MySqlExecuteTool.ToolName, MySqlWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (effective.PostgresTools)
        {
            var hosts = Postgres.PostgresConfigFile.LoadCatalog(profileDirectory, storageDirectory).Connections.SelectMany(c => Hosts(c.Config.Host, c.Config.Port ?? Postgres.PostgresConnectionConfig.DefaultPort));
            families.Add(new("PostgreSQL", Llm.Tools.PostgresQueryTool.ToolName, Llm.Tools.PostgresExecuteTool.ToolName, PostgresWords, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return families.Count == 0 ? null : new ServerDatabaseGuard(families);
    }

    /// <summary>What a host matches as: a loopback one only with its port (<c>host:port</c> and SQL Server's <c>host,port</c>), else the host alone.</summary>
    public static IEnumerable<string> Hosts(string? host, int port)
    {
        string name = host?.Trim() ?? "";
        if (name.Length == 0)
        {
            return [];
        }

        string at = port.ToString(CultureInfo.InvariantCulture);
        return Loopback.Contains(name) ? [name + ":" + at, name + "," + at] : [name];
    }

    /// <summary>A SQL Server <c>server</c>'s hosts: <c>host</c>, <c>host,port</c> or <c>host\instance</c> (1433 when no port is given).</summary>
    public static IEnumerable<string> SqlServerHosts(string? server)
    {
        string text = server?.Trim() ?? "";
        if (text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        int comma = text.IndexOf(',', StringComparison.Ordinal);
        string host = (comma >= 0 ? text[..comma] : text).Split('\\')[0].Trim();
        int port = comma >= 0 && int.TryParse(text[(comma + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 1433;
        return Hosts(host, port);
    }

    /// <summary>An Oracle data source's hosts: EZConnect's <c>host[:port]/service</c>, or every <c>HOST=</c> of a descriptor (1521 when no port is given).</summary>
    public static IEnumerable<string> OracleHosts(string? dataSource)
    {
        string text = dataSource?.Trim() ?? "";
        if (text.StartsWith('('))
        {
            var found = new List<string>();
            foreach (Match m in DescriptorHost().Matches(text))
            {
                int port = DescriptorPort().Match(text, m.Index) is { Success: true } p ? int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) : 1521;
                found.AddRange(Hosts(m.Groups[1].Value, port));
            }

            return found;
        }

        string hostPort = text.StartsWith("//", StringComparison.Ordinal) ? text[2..] : text;
        hostPort = hostPort.Split('/')[0];
        int colon = hostPort.LastIndexOf(':');
        return colon > 0 && int.TryParse(hostPort[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int at)
            ? Hosts(hostPort[..colon], at)
            : Hosts(hostPort, 1521);
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
    public static (string Family, string Token, string? File)? Judge(string command, string workdir, string root, ServerDatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (Find(command, guard) is { } found)
        {
            return (found.Family, found.Token, null);
        }

        foreach (var (name, text) in SqlitePolice.ScriptFiles(command, workdir, root))
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

    /// <summary>Whether <paramref name="host"/> stands in <paramref name="text"/> as a whole name: no name character beside it.</summary>
    private static bool FindHost(string text, string host)
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
