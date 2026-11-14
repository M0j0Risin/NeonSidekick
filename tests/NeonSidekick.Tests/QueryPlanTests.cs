using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.DuckDb;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Plans;
using NeonSidekick.Postgres;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The <c>*_explain</c> tools (2026-11-14, the database-tools plan's #12): the shared renderer (the header, the cap that keeps the flags and
/// the costliest steps, the estimates), each family's plan reader on a captured plan (<c>Fixtures/plans</c>), the prefixes and SQL Server's
/// batches, the refusals, SQLite and DuckDB end to end in process, and the classing (plan mode, the advisors' database list, help).
/// </summary>
public sealed class QueryPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "queryplan-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new() { SqliteTools = true, SqliteSandboxFiles = true, DuckDbTools = true, DuckDbSandboxFiles = true };

    public QueryPlanTests()
    {
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "shop.db"), Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER, total REAL);
            INSERT INTO customers VALUES (1, 'a'), (2, 'b');
            INSERT INTO orders VALUES (1, 1, 2.5), (2, 2, 7);
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "plans", name));

    private static async Task<string> Invoke(AIFunction tool, params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return (string)(await tool.InvokeAsync(arguments))!;
    }

    private SqliteExplainTool SqliteExplain() => new(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings);

    private DuckDbExplainTool DuckDbExplain() => new(new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files), () => _settings);

    // ── the renderer ────────────────────────────────────────────────────────

    [Fact]
    public void TheHeader_IsAWholeSentence_WithTheStepsRowsAndCost()
    {
        var plan = new QueryPlan([new QueryPlanStep(0, "Hash Join", null, 1204, 42.5), new QueryPlanStep(1, "Seq Scan", "ns_orders", 1204.4, 20)], [], 42.5, 1204);
        string answer = QueryPlanText.Answer(plan, "shop", "sales", TimeSpan.FromMilliseconds(12), 6000);
        string first = SqlText.Note(answer);
        Assert.Equal("Plan for shop/sales (estimated, not run; 12 ms): 2 steps, ~1204 rows, cost 42.5.", first);
        Assert.Contains("\n- Hash Join (~1204 rows, cost 42.5)\n  - Seq Scan on ns_orders (~1204 rows, cost 20)\n", answer);
        Assert.Contains(QueryPlanText.NoFlags, answer);
    }

    [Fact]
    public void ADatabaseNamedLikeItsConnection_IsWrittenOnce()
    {
        Assert.Equal("shop.db", QueryPlanText.Where("shop.db", ""));
        Assert.Equal("shop.db", QueryPlanText.Where("shop.db", "shop.db"));
        Assert.Equal("aw/AdventureWorks2022", QueryPlanText.Where("aw", "AdventureWorks2022"));
    }

    [Theory]
    [InlineData(33.3333, 33L)]
    [InlineData(0.2, 1L)]
    [InlineData(0.0, 0L)]
    [InlineData(1.2E+9, 1_200_000_000L)]
    [InlineData(1E+30, long.MaxValue)]
    [InlineData(-1.0, null)]
    [InlineData(double.NaN, null)]
    public void AnEstimate_IsWholeRows_AtLeastOneAboveZero_Clamped(double rows, long? expected) =>
        Assert.Equal(expected, QueryPlan.Estimate(rows));

    [Fact]
    public void ACount_PastTwoToTheThirtyOne_IsWrittenWhole() =>
        Assert.Equal("3000000000 rows", SqlText.Count(3_000_000_000L, "row"));

    [Fact]
    public void APlanTooLong_KeepsTheFlags_AndTheCostliestSteps_InTreeOrder()
    {
        var steps = Enumerable.Range(0, 200).Select(i => new QueryPlanStep(1, "Step " + i, "table_" + i, 10, i == 150 ? 1000 : i)).ToList();
        var flags = new[] { QueryPlanText.FullScan("big_table", 50_000) };
        string answer = QueryPlanText.Answer(new QueryPlan(steps, flags, 5000, null), "c", "d", TimeSpan.Zero, 1500);
        Assert.True(answer.Length <= 1500, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("A full scan of big_table (~50000 rows)", answer);
        Assert.Contains("Step 150 on table_150", answer);
        Assert.Contains("Step 199 on", answer);
        Assert.DoesNotContain("Step 3 on", answer);
        Assert.Matches(@"\(\d+ more steps left out\)", answer);
        Assert.True(answer.IndexOf("Step 150 ", StringComparison.Ordinal) < answer.IndexOf("Step 199 ", StringComparison.Ordinal));
    }

    [Fact]
    public void APlansNames_AreScrubbed_SoTheyCantReorderOrBreakTheAnswer()
    {
        var plan = new QueryPlan([new QueryPlanStep(0, "Seq Scan", "evil‮table\nline two", 5, null)], [QueryPlanText.Warning("bad\u001b[2Jthing")], null, null);
        string answer = QueryPlanText.Answer(plan, "c", "", TimeSpan.Zero, 6000);
        Assert.DoesNotContain('‮', answer);
        Assert.DoesNotContain('\u001b', answer);
        Assert.DoesNotContain("table\nline two", answer);
    }

    [Fact]
    public void AnUnreadablePlan_GivesItsStart_Fenced()
    {
        string answer = QueryPlanText.Unreadable("c", "d", TimeSpan.Zero, new string('x', 5000));
        Assert.StartsWith("Plan for c/d (estimated, not run; 0 ms): the plan couldn't be read", answer);
        Assert.Contains(UntrustedText.QuoteOpen, answer);
        Assert.True(answer.Length < 2300);
    }

    // ── the readers ─────────────────────────────────────────────────────────

    [Fact]
    public void DuckDb_ReadsItsJsonPlan_SkippingProjections_AndFlagsTheBigScanAndSort()
    {
        var plan = Assert.IsType<QueryPlan>(DuckDbPlan.Parse(Fixture("duckdb-join.json")));
        Assert.DoesNotContain(plan.Steps, s => s.Operation == "PROJECTION");
        Assert.Equal(["ORDER_BY", "HASH_GROUP_BY", "HASH_JOIN", "SEQ_SCAN", "SEQ_SCAN"], plan.Steps.Select(s => s.Operation));
        Assert.Equal([0, 1, 2, 3, 3], plan.Steps.Select(s => s.Depth));
        Assert.Equal("memory.main.orders", plan.Steps[3].Target);
        Assert.Equal(40000, plan.Steps[3].Rows);
        Assert.Contains(QueryPlanText.FullScan("memory.main.orders", 40000), plan.Flags);
        Assert.DoesNotContain(plan.Flags, f => f.Contains("customers", StringComparison.Ordinal));   // 5000 rows: under the line
        Assert.Contains(plan.Flags, f => f.StartsWith("A sort of ~30461 rows", StringComparison.Ordinal));
    }

    [Fact]
    public void DuckDb_AMergeJoin_FlagsOnlyTheBigScan()
    {
        var plan = Assert.IsType<QueryPlan>(DuckDbPlan.Parse(Fixture("duckdb-inequality-join.json")));
        Assert.Contains(plan.Steps, s => s.Operation == "PIECEWISE_MERGE_JOIN");
        Assert.Equal([QueryPlanText.FullScan("memory.main.orders", 200000)], plan.Flags);
    }

    [Fact]
    public void SqlServer_AScan_IsFlaggedByTheRowsItReads_WithTheMissingIndex()
    {
        // EstimateRows is what the scan keeps (15); the flag reads EstimatedRowsRead (every row of the table).
        var plan = Assert.IsType<QueryPlan>(SqlServerPlan.Parse(Fixture("sqlserver-missing-index.xml")));
        var scan = Assert.Single(plan.Steps);
        Assert.Equal("Clustered Index Scan", scan.Operation);
        Assert.Equal("Sales.SalesOrderDetail using PK_SalesOrderDetail_SalesOrderID_SalesOrderDetailID", scan.Target);
        Assert.Equal(15, QueryPlan.Estimate(scan.Rows));
        Assert.Equal(
            [QueryPlanText.MissingIndex("Sales.SalesOrderDetail", ["CarrierTrackingNumber"], [], [], 99.651), QueryPlanText.FullScan("Sales.SalesOrderDetail", 121317)],
            plan.Flags);
        Assert.Contains("about 100% cheaper", plan.Flags[0]);
    }

    [Fact]
    public void SqlServer_TheMissingIndex_NamesEachColumnsUse()
    {
        var plan = Assert.IsType<QueryPlan>(SqlServerPlan.Parse(Fixture("sqlserver-scan.xml")));
        Assert.Contains(QueryPlanText.MissingIndex("Sales.SalesOrderDetail", [], ["UnitPrice"], ["OrderQty"], 91.3578), plan.Flags);
        Assert.Equal(1.04932, plan.TotalCost);
    }

    [Fact]
    public void SqlServer_AKeyLookup_IsNamed_AndCountedOverItsRuns()
    {
        var plan = Assert.IsType<QueryPlan>(SqlServerPlan.Parse(Fixture("sqlserver-key-lookup.xml")));
        var lookup = Assert.Single(plan.Steps, s => s.Operation == "Key Lookup");
        Assert.Equal("Sales.SalesOrderDetail using PK_SalesOrderDetail_SalesOrderID_SalesOrderDetailID", lookup.Target);
        Assert.Contains(plan.Steps, s => s.Operation == "Index Seek" && s.Target == "Sales.SalesOrderDetail using IX_SalesOrderDetail_ProductID");
        Assert.Contains(QueryPlanText.KeyLookup("Sales.SalesOrderDetail using PK_SalesOrderDetail_SalesOrderID_SalesOrderDetailID", 75.6667), plan.Flags);
        Assert.True(plan.Steps.Single(s => s.Operation == "Key Lookup").Depth > plan.Steps[0].Depth);
    }

    [Fact]
    public void SqlServer_AnImplicitConversion_IsFlagged()
    {
        var plan = Assert.IsType<QueryPlan>(SqlServerPlan.Parse(Fixture("sqlserver-implicit-convert.xml")));
        Assert.Contains(plan.Flags, f => f.StartsWith("An implicit conversion (CONVERT_IMPLICIT(", StringComparison.Ordinal));
        Assert.Equal(plan.Flags.Count, plan.Flags.Distinct().Count());
    }

    [Fact]
    public void SqlServer_DeclaredValues_LeaveOnlyTheQuerysPlan()
    {
        var plan = Assert.IsType<QueryPlan>(SqlServerPlan.Parse(Fixture("sqlserver-join-declared.xml")));
        Assert.Equal("Nested Loops (Inner Join)", plan.Steps[0].Operation);
        Assert.Contains(plan.Steps, s => s.Operation == "Index Seek" && s.Target == "Sales.SalesOrderHeader using IX_SalesOrderHeader_CustomerID");
        Assert.Contains(plan.Steps, s => s.Operation == "Clustered Index Seek" && s.Target!.StartsWith("Sales.SalesOrderDetail", StringComparison.Ordinal));
        Assert.Empty(plan.Flags);
        Assert.Equal(6, QueryPlan.Estimate(plan.TotalRows));
    }

    [Fact]
    public void SqlServer_TheValuesAreTypedDeclarations_AsTheQueryBindsThem()
    {
        var parameters = new SqlParameterValue[] { new("s", "it's"), new("n", 5L), new("d", 12.50m), new("f", 0.5), new("b", true), new("z", null), new("long", new string('x', 4001)) };
        string batch = SqlServerPlan.WithDeclarations("SELECT 1", parameters);
        string[] lines = batch.Split('\n');
        Assert.Equal("DECLARE @s nvarchar(4000) = N'it''s';", lines[0]);
        Assert.Equal("DECLARE @n bigint = 5;", lines[1]);
        Assert.Equal("DECLARE @d decimal(38, 2) = 12.50;", lines[2]);
        Assert.Equal("DECLARE @f float = 0.5;", lines[3]);
        Assert.Equal("DECLARE @b bit = 1;", lines[4]);
        Assert.Equal("DECLARE @z nvarchar(4000) = NULL;", lines[5]);
        Assert.StartsWith("DECLARE @long nvarchar(max) = N'xxx", lines[6]);
        Assert.Equal("SELECT 1", lines[^1]);
        Assert.Equal("SELECT 1", SqlServerPlan.WithDeclarations("SELECT 1", []));
    }

    [Fact]
    public void Postgres_ReadsTheTree_AndFlagsTheBigScan()
    {
        var plan = Assert.IsType<QueryPlan>(PostgresPlan.Parse(Fixture("postgres-join.json")));
        Assert.Equal(["Sort", "Aggregate", "Hash Join", "Seq Scan", "Hash", "Seq Scan"], plan.Steps.Select(s => s.Operation));
        Assert.Equal([0, 1, 2, 3, 3, 4], plan.Steps.Select(s => s.Depth));
        Assert.Equal("big_orders", plan.Steps[3].Target);
        Assert.Equal([QueryPlanText.FullScan("big_orders", 199980)], plan.Flags);
        Assert.Equal(7992.32, plan.TotalCost);
    }

    [Fact]
    public void Postgres_AFilteredScanOfABigTable_IsFlagged_ThoughItKeepsFewRows()
    {
        var plan = Assert.IsType<QueryPlan>(PostgresPlan.Parse(Fixture("postgres-filtered-scan.json")));
        Assert.Equal([QueryPlanText.FilteredScan("big_orders", 40)], plan.Flags);
    }

    [Fact]
    public void Postgres_ANestedLoopOverABigInput_IsFlagged()
    {
        var plan = Assert.IsType<QueryPlan>(PostgresPlan.Parse(Fixture("postgres-nested-loop.json")));
        Assert.Contains(QueryPlanText.NestedLoop(200000), plan.Flags);
        Assert.Contains(QueryPlanText.FullScan("big_orders", 200000), plan.Flags);
    }

    [Theory]
    [InlineData("mysql-join.json")]
    [InlineData("mariadb-join.json")]
    public void MySqlAndMariaDb_ReadTheTables_AndFlagTheScanSortAndTemporaryTable(string fixture)
    {
        var plan = Assert.IsType<QueryPlan>(MySqlPlan.Parse(Fixture(fixture)));
        var scan = Assert.Single(plan.Steps, s => s.Operation == "Full table scan");
        Assert.Equal("o", scan.Target);
        Assert.Contains(plan.Steps, s => s.Operation == "Index lookup (eq_ref)" && s.Target == "c using PRIMARY");
        Assert.Contains(plan.Flags, f => f.StartsWith("A full scan of o (~", StringComparison.Ordinal));
        Assert.Contains(QueryPlanText.Filesort(null), plan.Flags);
        Assert.Contains(plan.Flags, f => f.StartsWith("A temporary table", StringComparison.Ordinal));
        Assert.NotNull(plan.TotalCost);
    }

    [Theory]
    [InlineData("mysql-join-buffer.json")]
    [InlineData("mariadb-join-buffer.json")]
    public void MySqlAndMariaDb_AJoinWithNoIndex_IsFlagged(string fixture)
    {
        var plan = Assert.IsType<QueryPlan>(MySqlPlan.Parse(Fixture(fixture)));
        Assert.Contains(plan.Flags, f => f.StartsWith("A join with no usable index on", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mysql-with.json")]
    [InlineData("mariadb-with.json")]
    public void MySqlAndMariaDb_AWithQuery_IsRead(string fixture)
    {
        var plan = Assert.IsType<QueryPlan>(MySqlPlan.Parse(Fixture(fixture)));
        Assert.Contains(plan.Steps, s => s.Target is { } t && t.Contains("big_orders", StringComparison.Ordinal) || s.Operation == "Full table scan");
    }

    [Fact]
    public void MySql_FormatTwo_IsRead()
    {
        const string json = """
            {"query": "...", "operation": "Nested loop inner join", "estimated_rows": 3, "estimated_total_cost": 12.5, "inputs": [
              {"operation": "Table scan on o", "access_type": "table", "table_name": "o", "estimated_rows": 50000, "estimated_total_cost": 5000},
              {"operation": "Single-row index lookup on c using PRIMARY", "access_type": "index", "table_name": "c", "index_name": "PRIMARY", "estimated_rows": 1}]}
            """;
        var plan = Assert.IsType<QueryPlan>(MySqlPlan.Parse(json));
        Assert.Equal(["Nested loop inner join", "Table scan on o", "Single-row index lookup on c using PRIMARY"], plan.Steps.Select(s => s.Operation));
        Assert.Equal("c using PRIMARY", plan.Steps[2].Target);
        Assert.Equal([QueryPlanText.FullScan("o", 50000)], plan.Flags);
        Assert.Equal(12.5, plan.TotalCost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"a\": 1}")]
    [InlineData("[]")]
    public void AReader_GivenSomethingElse_SaysSo(string text)
    {
        Assert.Null(DuckDbPlan.Parse(text));
        Assert.Null(PostgresPlan.Parse(text));
        Assert.Null(MySqlPlan.Parse(text));
        Assert.Null(SqlServerPlan.Parse(text));
    }

    [Fact]
    public void Sqlite_RebuildsTheTree_AndFlagsTheScanAndTheTempBTree()
    {
        var grid = new SqlGrid(["id", "parent", "notused", "detail"],
        [
            ["2", "0", "0", "SCAN o"],
            ["4", "0", "0", "SEARCH c USING INTEGER PRIMARY KEY (rowid=?)"],
            ["9", "0", "0", "USE TEMP B-TREE FOR ORDER BY"],
            ["12", "4", "0", "SCAN CONSTANT ROW"],
            ["15", "0", "0", "SEARCH t USING AUTOMATIC COVERING INDEX (k=?)"],
        ], false);
        var plan = Assert.IsType<QueryPlan>(SqlitePlan.Parse(grid));
        Assert.Equal([0, 0, 0, 1, 0], plan.Steps.Select(s => s.Depth));
        Assert.Equal(
            [QueryPlanText.FullScan("o", null), QueryPlanText.TempBTree("for order by"), QueryPlanText.AutomaticIndex("t")],
            plan.Flags);
    }

    // ── the prefixes and batches (contracts) ────────────────────────────────

    [Fact]
    public void TheExplainPrefixes_PlanAndNeverRun()
    {
        Assert.Equal("EXPLAIN (FORMAT JSON) ", PostgresPlan.Prefix);
        Assert.Equal("EXPLAIN FORMAT=JSON ", MySqlPlan.Prefix);
        Assert.Equal("EXPLAIN QUERY PLAN ", SqlitePlan.Prefix);
        Assert.Equal("EXPLAIN (FORMAT json) ", DuckDbPlan.Prefix);
        Assert.All(new[] { PostgresPlan.Prefix, MySqlPlan.Prefix, SqlitePlan.Prefix, DuckDbPlan.Prefix }, p => Assert.DoesNotContain("ANALYZE", p, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["SET SHOWPLAN_XML ON", "SELECT 1", "SET SHOWPLAN_XML OFF"], SqlServerPlan.Batches("SELECT 1"));
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", true)]
    [InlineData("  (with x as (select 1) select * from x)", true)]
    [InlineData("SELECT 1", false)]
    [InlineData("WITHOUT", false)]
    public void MySql_KnowsAWithLead(string sql, bool expected) => Assert.Equal(expected, MySqlExplainTool.LeadsWithWith(sql));

    // ── SQLite and DuckDB end to end ────────────────────────────────────────

    [Fact]
    public async Task Sqlite_ExplainsAJoin_WithoutRunningIt()
    {
        string answer = await Invoke(SqliteExplain(), ("sql", "SELECT c.name, o.total FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.total > @t"), ("database", "shop.db"), ("params", System.Text.Json.JsonDocument.Parse("""{"t": 1}""").RootElement));
        Assert.StartsWith("Plan for shop.db (estimated, not run; ", answer);
        Assert.Contains("SCAN o", answer);
        Assert.Contains("SEARCH c USING INTEGER PRIMARY KEY", answer);
        Assert.Contains(QueryPlanText.FullScan("o", null), answer);
    }

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("EXPLAIN SELECT 1")]
    public async Task Sqlite_RefusesWhatTheQueryToolRefuses(string sql)
    {
        string answer = await Invoke(SqliteExplain(), ("sql", sql), ("database", "shop.db"));
        Assert.StartsWith("Error:", answer);
    }

    [Fact]
    public async Task DuckDb_ExplainsAQuery_OverACsvFile_ThroughTheFileRules()
    {
        File.WriteAllText(Path.Combine(_root, "sales.csv"), "id,total\n1,2.5\n2,7\n");
        string answer = await Invoke(DuckDbExplain(), ("sql", "SELECT sum(total) FROM 'sales.csv' WHERE id > $id"), ("params", System.Text.Json.JsonDocument.Parse("""{"id": 0}""").RootElement));
        Assert.StartsWith("Plan for ", answer);
        Assert.Contains("(estimated, not run; ", SqlText.Note(answer));
        Assert.Contains("READ_CSV", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, answer);
    }

    [Theory]
    [InlineData("EXPLAIN SELECT 1", "EXPLAIN")]
    [InlineData("EXPLAIN ANALYZE SELECT 1", "EXPLAIN")]
    [InlineData("SUMMARIZE SELECT 1", "SUMMARIZE")]
    [InlineData("DESCRIBE SELECT 1", "DESCRIBE")]
    [InlineData("SHOW TABLES", "SHOW")]
    public async Task DuckDb_RefusesALeadWithNoPlan_OrOneThatWouldRun(string sql, string word)
    {
        Assert.Equal(QueryPlanText.NotPlannable(word), await Invoke(DuckDbExplain(), ("sql", sql), ("database", ":memory:")));
    }

    [Fact]
    public async Task DuckDb_RefusesAFileOutsideTheWorkingDirectory()
    {
        string answer = await Invoke(DuckDbExplain(), ("sql", "SELECT * FROM '../../outside.csv'"), ("database", ":memory:"));
        Assert.StartsWith("Error:", answer);
    }

    [Fact]
    public void DuckDb_ExplainStillReturnsTwoColumns_ThePlanInTheLast()
    {
        // The reader reads by position (the last column); a version that changes the shape fails here first.
        var target = new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files).Resolve(":memory:", out _)!;
        var run = DuckDbAccess.Run(target, [DuckDbPrepared.AsWritten(DuckDbPlan.Prefix + "SELECT 42")], [], 10, 30, _root, CancellationToken.None, QueryPlan.WholeCell);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal(["explain_key", "explain_value"], run.Grids[0].Columns);
        using var json = System.Text.Json.JsonDocument.Parse(QueryPlan.TextOf(run, -1));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, json.RootElement.ValueKind);
    }

    // ── classing ────────────────────────────────────────────────────────────

    public static TheoryData<string> ExplainNames() =>
        [SqlExplainTool.ToolName, MySqlExplainTool.ToolName, PostgresExplainTool.ToolName, SqliteExplainTool.ToolName, DuckDbExplainTool.ToolName];

    [Theory]
    [MemberData(nameof(ExplainNames))]
    public void EachExplain_IsAPlanModeRead_KeptFromAdvisors_AndInHelp(string name)
    {
        Assert.True(PlanTools.Allowed(name), name);
        Assert.Contains(name, ChatScreen.DatabaseToolNames);
        Assert.Contains(NeonSidekick.Help.HelpToolCatalog.All, t => t.Name == name);
    }

    [Fact]
    public void TheFactories_OfferExplain_AfterTheQuery()
    {
        var sqlite = ChatScreen.SqliteTools(new SqliteAccess(() => SqliteCatalog.Empty, () => _files), () => _settings, allow: null).Select(t => t.Name).ToList();
        Assert.Equal(sqlite.IndexOf(SqliteQueryTool.ToolName) + 1, sqlite.IndexOf(SqliteExplainTool.ToolName));
        var duck = ChatScreen.DuckDbTools(new DuckDbAccess(() => DuckDbCatalog.Empty, () => _files), () => _settings, allow: null).Select(t => t.Name).ToList();
        Assert.Equal(duck.IndexOf(DuckDbQueryTool.ToolName) + 1, duck.IndexOf(DuckDbExplainTool.ToolName));
        Assert.Contains(SqliteExplainTool.ToolName, ChatScreen.SqliteToolNames);
        Assert.Contains(DuckDbExplainTool.ToolName, ChatScreen.DuckDbToolNames);
    }
}
