using NeonSidekick.Files;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// The package-manager commands the database police let through (2026-10-10, the user's call, after <c>dotnet add package
/// Microsoft.Data.Sqlite</c> was refused): a package segment is blanked, the rest of the line still judged, and a segment with a shell
/// operator or substitution never cut.
/// </summary>
public sealed class PackageCommandsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-packages-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static readonly ServerDatabaseGuard.Family Postgres = new("PostgreSQL", "postgres_query", "postgres_execute", ServerDatabasePolice.PostgresWords, ["pg01"]);
    private static readonly ServerDatabaseGuard.Family SqlServer = new("SQL Server", "sql_query", "sql_execute", ServerDatabasePolice.SqlServerWords, []);

    [Theory]
    [InlineData("dotnet add package Microsoft.Data.Sqlite")]
    [InlineData("dotnet add src/App.csproj package Npgsql --version 8.0.0")]
    [InlineData("dotnet remove package Microsoft.Data.SqlClient")]
    [InlineData("dotnet list package --include-transitive")]
    [InlineData("dotnet restore")]
    [InlineData("dotnet tool install --global dotnet-ef")]
    [InlineData("nuget install System.Data.SQLite")]
    [InlineData("Install-Package System.Data.SQLite -Version 1.0.118")]
    [InlineData("& \"C:\\tools\\nuget.exe\" install SQLitePCLRaw.bundle_e_sqlite3")]
    [InlineData("pip install psycopg[binary]")]
    [InlineData("pip3 uninstall pymysql")]
    [InlineData("python -m pip install \"sqlite-utils>=3\"")]
    [InlineData("py -m pip show oracledb")]
    [InlineData("uv add psycopg")]
    [InlineData("uv pip install sqlite-utils")]
    [InlineData("poetry add asyncpg")]
    [InlineData("npm i better-sqlite3")]
    [InlineData("npm install --save-dev @types/better-sqlite3")]
    [InlineData("pnpm add pg")]
    [InlineData("yarn add mysql2")]
    [InlineData("cargo add rusqlite")]
    [InlineData("gem install sqlite3")]
    [InlineData("go get github.com/mattn/go-sqlite3")]
    public void APackageCommand_IsCut(string line)
    {
        Assert.True(PackageCommands.IsPackageCommand(line), line);
        Assert.Equal("", PackageCommands.Strip(line));
    }

    [Theory]
    [InlineData("sqlite3 shop.db .tables")]
    [InlineData("dotnet sqlite")]
    [InlineData("dotnet run --project tools/SqliteSeed")]
    [InlineData("pip-sqlite install x")]
    [InlineData("npm run sqlite")]
    [InlineData("pip install $(sqlite3 shop.db 'select 1')")]
    [InlineData("pip install `sqlite3 shop.db`")]
    [InlineData("pip install \"$(sqlite3 shop.db)\"")]
    [InlineData("pip install sqlite-utils > shop.db")]
    [InlineData("pip install x <(sqlite3 shop.db)")]
    [InlineData("pip install x & sqlite3 shop.db")]
    [InlineData("Install-Package @(sqlite3)")]
    public void ALookAlike_OrAnOperator_IsKept(string line)
    {
        Assert.False(PackageCommands.IsPackageCommand(line), line);
        Assert.Equal(line, PackageCommands.Strip(line));
    }

    [Fact]
    public void AChainedLine_KeepsItsOtherSegments()
    {
        Assert.Equal("&& sqlite3 shop.db", PackageCommands.Strip("dotnet add package Microsoft.Data.Sqlite && sqlite3 shop.db"));
        Assert.Equal("dotnet build;", PackageCommands.Strip("dotnet build; pip install sqlite-utils"));
        Assert.Equal("| sqlite3", PackageCommands.Strip("npm i better-sqlite3 | sqlite3"));
        Assert.Equal("\n", PackageCommands.Strip("dotnet add package Npgsql\ndotnet add package Microsoft.Data.Sqlite"));
        Assert.Contains("dotnet add|remove|list [<project>] package", PackageCommands.Forms);
    }

    [Fact]
    public void TheSqlitePolice_PassAPackageCommand_AndStillJudgeTheRest()
    {
        Assert.Null(SqlitePolice.Judge("dotnet add package Microsoft.Data.Sqlite", _dir, _dir, DatabaseGuard.ByExtension));
        Assert.Null(SqlitePolice.Judge("pip install sqlite-utils && dotnet build", _dir, _dir, DatabaseGuard.ByExtension));
        Assert.Equal(("sqlite3", (string?)null), SqlitePolice.Judge("dotnet add package Microsoft.Data.Sqlite && sqlite3 shop.db", _dir, _dir, DatabaseGuard.ByExtension));
        Assert.Equal(("shop.db", (string?)null), SqlitePolice.Judge("npm i better-sqlite3; cat shop.db", _dir, _dir, DatabaseGuard.ByExtension));

        File.WriteAllText(Path.Combine(_dir, "app.py"), "import sqlite3\n");
        Assert.Equal(("sqlite3", "app.py"), SqlitePolice.Judge("pip install sqlite-utils && python app.py", _dir, _dir, DatabaseGuard.ByExtension));   // the code that uses it is still read
    }

    [Fact]
    public void TheServerDatabasePolice_PassAPackageCommand_AndStillJudgeTheRest()
    {
        var guard = new ServerDatabaseGuard([Postgres, SqlServer]);
        Assert.Null(ServerDatabasePolice.Judge("dotnet add package Npgsql", _dir, _dir, guard));
        Assert.Null(ServerDatabasePolice.Judge("dotnet add package Microsoft.Data.SqlClient", _dir, _dir, guard));
        Assert.Null(ServerDatabasePolice.Judge("pip install psycopg[binary]", _dir, _dir, guard));
        Assert.Equal(("PostgreSQL", "psql", (string?)null), ServerDatabasePolice.Judge("pip install psycopg && psql -h pg01", _dir, _dir, guard));
    }

    [Fact]
    public void TheRefusals_SayAPackageCommandIsAllowed()
    {
        Assert.EndsWith("Installing or removing a package is allowed: run the package command on its own.", ShellText.SqlitePoliced);
        Assert.EndsWith("Installing or removing a package is allowed: run the package command on its own.", ShellText.ServerDatabasePoliced(Postgres));
    }
}
