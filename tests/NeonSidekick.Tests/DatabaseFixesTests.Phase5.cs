using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using MySqlConnector;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Phase 5 of the database review's fixes (2026-10-24): results and catalog answers — mechanism 5.</summary>
public sealed class DatabaseResultsTests
{
    /// <summary>B10, F15, N8: a cell is cut without splitting a pair, scrubbed of controls and reorders, and a text NULL is told from a real one.</summary>
    [Fact]
    public void ACell_IsCutWhole_Scrubbed_AndATextNullIsQuoted()
    {
        string cut = SqlText.Cell(new string('x', SqlText.MaxCellChars - 1) + "😀😀");
        Assert.StartsWith(new string('x', SqlText.MaxCellChars - 1) + "…", cut);   // the pair at the edge left out whole
        Assert.Equal("a�b\\u202Ec", SqlText.Cell("a\u001Bb‮c"));
        Assert.Equal("'NULL'", SqlText.Cell("NULL"));
        Assert.Equal(SqlText.Null, SqlText.Cell(DBNull.Value));
    }

    /// <summary>F15: a catalog run's names are one line each, their hiding characters escaped; NULL stays NULL.</summary>
    [Fact]
    public void ACatalogRun_MakesEachNameOneLine()
    {
        var run = new SqlRun(SqlOutcome.Ok, "", "c", "d", [new SqlGrid(["name"], [["evil\nForeign keys:\n- forged"], [SqlText.Null], ["bidi‮exe"]], false)], TimeSpan.Zero);
        var rows = SqlText.CatalogRun(run).Grids[0].Rows;
        Assert.Equal("evil Foreign keys: - forged", rows[0][0]);
        Assert.Equal(SqlText.Null, rows[1][0]);
        Assert.Equal("bidi\\u202Eexe", rows[2][0]);
    }

    /// <summary>E7: a table's header stays within the cap: long names clipped, columns that do not fit left out and said.</summary>
    [Fact]
    public void ATablesHeader_StaysWithinTheCap()
    {
        var columns = Enumerable.Range(0, 40).Select(i => "column_" + i + new string('c', 100)).ToList();
        var grid = new SqlGrid(columns, [[.. columns.Select(_ => "v")]], false);
        string table = SqlText.Table(grid, 600, out _);
        Assert.Contains(SqlText.ColumnsLeftOut(columns.Count - table.Split('\n')[0].Split(" | ").Length), table);
        Assert.True(table.Split('\n')[0].Length <= 600);
        Assert.DoesNotContain(new string('c', SqlText.MaxColumnNameChars + 1), table);
    }

    /// <summary>B12: a number past a double's range is a bad value, never a throw.</summary>
    [Fact]
    public void AParamPastADoublesRange_IsABadValue()
    {
        using var document = JsonDocument.Parse("""{ "big": 1e400 }""");
        Assert.Null(SqlQueryTool.ReadParameters(document.RootElement, out string? error));
        Assert.Equal(SqlText.BadParamValue("big"), error);
    }

    /// <summary>P2: Oracle's params take <c>:id</c> as well as <c>@id</c> and <c>id</c>; a bad one is worded for <c>:name</c>.</summary>
    [Fact]
    public void OracleParams_TakeAColonName()
    {
        using var document = JsonDocument.Parse("""{ ":id": 5, "@n": "x", "plain": true }""");
        var read = SqlQueryTool.ReadParameters(document.RootElement, out _, SqlQueryTool.ReadColonName, OracleText.BadParamName);
        Assert.Equal(["id", "n", "plain"], read!.Select(p => p.Name));
        using var bad = JsonDocument.Parse("""{ ":1x": 5 }""");
        Assert.Null(SqlQueryTool.ReadParameters(bad.RootElement, out string? error, SqlQueryTool.ReadColonName, OracleText.BadParamName));
        Assert.Equal(OracleText.BadParamName(":1x"), error);
    }

    /// <summary>E10: a text bound to Oracle goes as a CLOB once its UTF-8 is past 4,000 bytes, whatever its length in characters.</summary>
    [Fact]
    public void OracleText_PastFourThousandBytes_IsBoundAsAClob()
    {
        Assert.Equal(global::Oracle.ManagedDataAccess.Client.OracleDbType.Clob, OracleAccess.Bind(new SqlParameterValue("p", new string('€', 2000))).OracleDbType);   // 6,000 bytes
        Assert.Equal(global::Oracle.ManagedDataAccess.Client.OracleDbType.Varchar2, OracleAccess.Bind(new SqlParameterValue("p", new string('x', 4000))).OracleDbType);
    }

    /// <summary>B9: an interval of years and months as Oracle writes it.</summary>
    [Fact]
    public void AnOracleIntervalYm_ReadsAsYearsAndMonths()
    {
        Assert.Equal("+1-06", OracleAccess.YearsMonths(1, 6));
        Assert.Equal("-2-03", OracleAccess.YearsMonths(-2, -3));
    }

    /// <summary>B11 and O10: an array keeps its dimensions and its UTC, and is clipped whole.</summary>
    [Fact]
    public void APostgresArray_KeepsItsDimensions_AndIsClippedWhole()
    {
        Assert.Equal("{{1, 2}, {3, 4}}", PostgresAccess.ArrayText(new[,] { { 1, 2 }, { 3, 4 } }));
        Assert.Equal("{2009-01-07 13:05:09 UTC}", PostgresAccess.ArrayText(new[] { new DateTime(2009, 1, 7, 13, 5, 9, DateTimeKind.Utc) }));
        string big = PostgresAccess.ArrayText(Enumerable.Range(0, 100_000).ToArray());
        Assert.EndsWith(SqlText.MoreChars(SqlText.MaxCellChars), big);
        Assert.True(big.Length < SqlText.MaxCellChars + 40);
    }

    /// <summary>O14: a quoted name keeps the spaces inside its quotes; spaces outside them go.</summary>
    [Fact]
    public void AQuotedName_KeepsItsSpaces()
    {
        Assert.Equal(("sales", " spaced "), PostgresAccess.Split(" sales . \" spaced \" ")!.Value);
        Assert.Equal(("shop", " t "), MySqlAccess.Split("shop.` t `")!.Value);
        Assert.Null(PostgresAccess.Split("\"  \"."));
    }

    /// <summary>N6 and N14: EXECUTE is a write power; a column list's commas do not split a privilege.</summary>
    [Fact]
    public void MySqlWritePowers_CountExecute_AndReadColumnLists()
    {
        Assert.Equal(["INSERT ON `shop`.*"], MySqlText.WritePowers(["GRANT SELECT (id, name), INSERT ON `shop`.* TO `u`@`%`"]));
        Assert.Equal(["EXECUTE ON `shop`.*"], MySqlText.WritePowers(["GRANT EXECUTE ON `shop`.* TO `u`@`%`"]));
    }

    /// <summary>N9 and N10: a TINYINT(1) reads as its number, a zero date as MySQL writes it.</summary>
    [Fact]
    public void MySql_ReadsTinyIntsAsNumbers_AndZeroDatesAsThemselves()
    {
        var builder = new MySqlConnectionConfig { Host = "h", Database = "d", User = "u" }.Builder("p");
        Assert.False(builder.TreatTinyAsBoolean);
        Assert.True(builder.AllowZeroDateTime);
        Assert.False(builder.ConvertZeroDateTime);
        Assert.Equal("0000-00-00", MySqlAccess.DateCell(new MySqlDateTime(0, 0, 0, 0, 0, 0, 0)));
        Assert.Equal("2026-10-24", MySqlAccess.DateCell(new MySqlDateTime(2026, 10, 24, 0, 0, 0, 0)));
    }

    /// <summary>O9: a SQLite foreign key that names no column points at its parent's primary key, said so.</summary>
    [Fact]
    public async Task ASqliteKeyToTheParentsKey_SaysSo()
    {
        string dir = Directory.CreateTempSubdirectory("neon-o9-").FullName;
        try
        {
            string path = Path.Combine(dir, "fk.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE parent (id INTEGER PRIMARY KEY); CREATE TABLE child (p INTEGER REFERENCES parent);";
                command.ExecuteNonQuery();
            }

            var settings = new AppSettingsData { SqliteTools = true, SqliteSandboxFiles = true };
            var access = new SqliteAccess(() => SqliteCatalog.Empty, () => new WorkingDirectory(() => dir, new ManualTimeProvider()));
            string described = (string)(await new SqliteDescribeTool(access, () => settings).InvokeAsync(new AIFunctionArguments { ["database"] = "fk.db", ["table"] = "child" }))!;
            Assert.Contains(SqliteCatalogQueries.ParentKey, described);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
