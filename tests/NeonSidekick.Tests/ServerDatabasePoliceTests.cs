using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>The shell police's server-database rule (2026-10-05): the families' clients, drivers and hosts, while their tools are on.</summary>
public sealed class ServerDatabasePoliceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-dbpolice-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static ServerDatabaseGuard Guard(params ServerDatabaseGuard.Family[] families) => new(families);

    private static readonly ServerDatabaseGuard.Family Postgres = new("PostgreSQL", "postgres_query", "postgres_execute", ServerDatabasePolice.PostgresWords, ["db01.example.com", "localhost:5432", "localhost,5432"]);

    private static readonly ServerDatabaseGuard.Family SqlServer = new("SQL Server", "sql_query", "sql_execute", ServerDatabasePolice.SqlServerWords, []);

    [Theory]
    [InlineData("psql -h db -U app -c \"DELETE FROM t\"", "psql")]
    [InlineData("PGPASSWORD=x pg_dump shop > shop.sql", "pg_dump")]
    [InlineData("import psycopg2\npsycopg2.connect('dbname=shop')", "psycopg2")]
    [InlineData("const { Client } = require('postgres')", "postgres")]
    [InlineData("docker exec postgres_dev psql", "postgres_dev")]
    [InlineData("dotnet add package Npgsql", "Npgsql")]
    [InlineData("curl http://db01.example.com/x", "db01.example.com")]
    [InlineData("nc localhost:5432", "localhost:5432")]
    public void Find_AClientADriverOrAHost(string text, string token)
    {
        var found = ServerDatabasePolice.Find(text, Guard(Postgres));
        Assert.Equal(("PostgreSQL", token), found);
    }

    [Theory]
    [InlineData("curl http://localhost:8080/health")]   // a loopback host only with its port
    [InlineData("echo psqlx")]                          // a short word only standing alone
    [InlineData("cat db01.example.com.bak2")]           // a host only as a whole name
    [InlineData("git status")]
    public void Find_PassesWhatReachesNoDatabase(string text) => Assert.Null(ServerDatabasePolice.Find(text, Guard(Postgres)));

    [Fact]
    public void Find_KnowsEachFamilysWords()
    {
        Assert.Equal(("SQL Server", "Microsoft.Data.SqlClient"), ServerDatabasePolice.Find("using Microsoft.Data.SqlClient;", Guard(SqlServer)));
        Assert.Equal(("SQL Server", "sqlcmd"), ServerDatabasePolice.Find("sqlcmd -S . -Q \"DROP TABLE t\"", Guard(SqlServer)));
        Assert.Equal(("SQL Server", "bcp"), ServerDatabasePolice.Find("bcp t out x.dat", Guard(SqlServer)));
        Assert.Null(ServerDatabasePolice.Find("abcpq", Guard(SqlServer)));
        var oracle = new ServerDatabaseGuard.Family("Oracle", "oracle_query", "oracle_execute", ServerDatabasePolice.OracleWords, []);
        Assert.Equal(("Oracle", "cx_Oracle"), ServerDatabasePolice.Find("import cx_Oracle", Guard(oracle)));
        Assert.Equal(("Oracle", "sqlcl"), ServerDatabasePolice.Find("sqlcl neon@db", Guard(oracle)));
        var mysql = new ServerDatabaseGuard.Family("MySQL", "mysql_query", "mysql_execute", ServerDatabasePolice.MySqlWords, []);
        Assert.Equal(("MySQL", "pymysql"), ServerDatabasePolice.Find("import pymysql", Guard(mysql)));
        Assert.Equal(("MySQL", "mariadb"), ServerDatabasePolice.Find("mariadb -u root", Guard(mysql)));
        Assert.Null(ServerDatabasePolice.Find("psql", Guard(mysql)));   // a family off is not looked for
    }

    [Fact]
    public void TheHosts_ALoopbackOneWithItsPort_SqlServersAndOraclesForms()
    {
        Assert.Equal(["db"], ServerDatabasePolice.Hosts("db", 5432));
        Assert.Equal(["localhost:3306", "localhost,3306"], ServerDatabasePolice.Hosts(" localhost ", 3306));
        Assert.Empty(ServerDatabasePolice.Hosts(" ", 1));
        Assert.Equal(["sqlhost01"], ServerDatabasePolice.SqlServerHosts(@"sqlhost01\SQLEXPRESS"));
        Assert.Equal(["127.0.0.1:1433", "127.0.0.1,1433"], ServerDatabasePolice.SqlServerHosts("127.0.0.1,1433"));
        Assert.Equal([".:1433", ".,1433"], ServerDatabasePolice.SqlServerHosts("."));
        Assert.Equal(["localhost:1521", "localhost,1521"], ServerDatabasePolice.OracleHosts("localhost:1521/FREEPDB1"));
        Assert.Equal(["dbhost01.example.com"], ServerDatabasePolice.OracleHosts("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))"));
        Assert.Equal(["ora"], ServerDatabasePolice.OracleHosts("ora/XE"));
    }

    [Fact]
    public void For_TheFamiliesWhoseToolsAreOn_TheirHostsFromEveryConnection()
    {
        string profile = Path.Combine(_dir, "profiles", "default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "postgres.json"), """{ "connections": { "shop": { "host": "pg01", "user": "u" }, "local": { "host": "localhost", "port": 5433, "user": "u" } } }""");
        File.WriteAllText(Path.Combine(_dir, "mysql.json"), """{ "connections": { "m": { "host": "my01", "user": "u" } } }""");
        Assert.Null(ServerDatabasePolice.For(new AppSettingsData(), profile, _dir));

        var guard = ServerDatabasePolice.For(new AppSettingsData { PostgresTools = true, MySqlTools = true }, profile, _dir)!;
        Assert.Equal(["MySQL", "PostgreSQL"], guard.Families.Select(f => f.Title));
        Assert.Equal(["my01"], guard.Families[0].Hosts);
        Assert.Equal(["pg01", "localhost:5433", "localhost,5433"], guard.Families[1].Hosts);   // offered or not
        Assert.Equal("mysql_execute", ServerDatabasePolice.FamilyOf(guard, "MySQL").ExecuteTool);
    }

    [Fact]
    public void Judge_ReadsTheScriptFilesALineRuns()
    {
        File.WriteAllText(Path.Combine(_dir, "load.py"), "import psycopg\npsycopg.connect('host=pg01').execute('DELETE FROM t')\n");
        Assert.Equal(("PostgreSQL", "psycopg", "load.py"), ServerDatabasePolice.Judge("python load.py", _dir, _dir, Guard(Postgres)));
        Assert.Equal(("PostgreSQL", "psql", (string?)null), ServerDatabasePolice.Judge("psql", _dir, _dir, Guard(Postgres)));
        Assert.Null(ServerDatabasePolice.Judge("python other.py", _dir, _dir, Guard(Postgres)));
    }

    [Fact]
    public void TheWording_AndTheRules()
    {
        Assert.Equal(
            "Error: refused by the shell police — database: while the PostgreSQL tools are on, a PostgreSQL database is reached only through them — postgres_query to read, and postgres_execute to change one when its mode allows it. Do not try another way (a client, a script, another driver); tell the user if a change is needed that the tools refuse.",
            ShellText.ServerDatabasePoliced(Postgres));
        Assert.True(ShellText.IsPoliced(ShellText.ServerDatabasePoliced(Postgres)));
        Assert.Equal("PostgreSQL: 'psql' in load.py — not run", ShellText.ServerDatabaseShown("PostgreSQL", "psql", "load.py"));
        Assert.Equal("MySQL: 'mysql' — not run", ShellText.ServerDatabaseShown("MySQL", "mysql", null));
        foreach (var (rule, name) in new[] { (Assistant.SqlRule, "SQL Server"), (Assistant.OracleRule, "Oracle"), (Assistant.MySqlRule, "MySQL"), (Assistant.PostgresRule, "PostgreSQL") })
        {
            Assert.EndsWith($"Reach {name} only through these tools — never a client or a script in the shell, which are refused.", rule);
        }
    }
}
