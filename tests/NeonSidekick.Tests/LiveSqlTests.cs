using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a SQL Server with AdventureWorks is reachable (2026-09-23):
/// <c>NEONSIDEKICK_TEST_SQL_CONNECTION</c> holds a SqlClient connection string to it (the dev container:
/// <c>Server=127.0.0.1,1433;Database=AdventureWorks2022;User ID=sa;Password=…;TrustServerCertificate=true;Encrypt=optional</c>),
/// and one connect in 5 s proves it. Skipped without; never a CI safety net.
/// </summary>
internal static class LiveSql
{
    public const string Variable = "NEONSIDEKICK_TEST_SQL_CONNECTION";

    public static readonly SqlConnectionConfig? Config;

    /// <summary>The password as the variable gives it; <see cref="Config"/> holds it DPAPI-encrypted, the way sql.json keeps it (later on 2026-09-23), so every live call decrypts it.</summary>
    public static readonly string Password = "";
    public static readonly string Unavailable = "";

    static LiveSql()
    {
        string? text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(text);
            var config = new SqlConnectionConfig
            {
                Server = builder.DataSource,
                Database = builder.InitialCatalog,
                Auth = builder.IntegratedSecurity ? SqlConnectionConfig.WindowsAuth : SqlConnectionConfig.SqlAuth,
                User = builder.UserID,
                Password = WindowsCredentials.Protect(builder.Password).Value ?? builder.Password,
                TrustServerCertificate = builder.TrustServerCertificate,
                Encrypt = builder.Encrypt == SqlConnectionEncryptOption.Optional ? "optional" : builder.Encrypt == SqlConnectionEncryptOption.Strict ? "strict" : "mandatory",
                ConnectTimeoutSeconds = 5,
            };
            using var connection = new SqlConnection(config.Builder(password: builder.Password).ConnectionString);
            Password = builder.Password;
            connection.Open();
            Config = config;
        }
        catch (Exception ex) when (ex is SqlException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Unavailable = "No SQL Server: " + ex.Message;
        }
    }
}

/// <summary>Skips unless <see cref="LiveSql"/> connected: the SQL tools against a real server.</summary>
public sealed class LiveSqlFactAttribute : FactAttribute
{
    public LiveSqlFactAttribute()
    {
        if (LiveSql.Config is null)
        {
            Skip = LiveSql.Unavailable;
        }
    }
}

/// <summary>The six SQL tools against AdventureWorks2022 (2026-09-23): what each returns, and the layers under the gate.</summary>
public sealed class LiveSqlTests
{
    private readonly AppSettingsData _settings = new();
    private readonly SqlAccess _access;
    private readonly IReadOnlyList<AIFunction> _tools;

    public LiveSqlTests()
    {
        var catalog = LiveSql.Config is { } config ? new SqlCatalog([new SqlNamedConnection("aw", config, "test")], []) : SqlCatalog.Empty;
        _access = new SqlAccess(() => catalog);
        _tools = ChatScreen.SqlTools(_access, () => _settings);
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    /// <summary>A cross join big enough to run for minutes: what a timeout and a cancel stop (<c>--sql-check</c>'s too, since 2026-10-03).</summary>
    private const string Heavy = SqlCheck.SlowQuery;

    [LiveSqlFact]
    public async Task TheCatalog_FromServerToTable()
    {
        string connections = await Invoke<SqlConnectionsTool>();
        Assert.StartsWith("1 SQL connection (", connections);
        Assert.DoesNotContain(LiveSql.Password, connections);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, LiveSql.Config!.Password);

        string databases = await Invoke<SqlDatabasesTool>();
        Assert.Matches(@"^\d+ databases in aw/AdventureWorks2022\n", databases);
        Assert.Contains("| AdventureWorks2022 | ONLINE |", databases);

        string tables = await Invoke<SqlTablesTool>(("pattern", "SalesOrder"));
        Assert.Contains("| Sales | SalesOrderHeader | table | 31465 |", tables);
        Assert.Contains("| Sales | SalesOrderDetail | table |", tables);
        Assert.DoesNotContain("| Person |", tables);
        Assert.Contains("| Sales | vSalesPerson | view | NULL |", await Invoke<SqlTablesTool>(("schema", "Sales"), ("pattern", "vSales*")));

        string header = await Invoke<SqlDescribeTool>(("table", "Sales.SalesOrderHeader"));
        Assert.StartsWith("Sales.SalesOrderHeader (table, 26 columns) in aw/AdventureWorks2022\n", header);
        Assert.Contains("| SalesOrderID | int identity | no | PK |  |", header);
        Assert.Contains("| OrderDate | datetime | no |  | (getdate()) |", header);
        Assert.Contains("| TotalDue | money computed | no |  |  |", header);
        Assert.Contains("- out: Sales.SalesOrderHeader.CustomerID -> Sales.Customer.CustomerID (FK_SalesOrderHeader_Customer_CustomerID)", header);
        Assert.Contains("- in : Sales.SalesOrderDetail.SalesOrderID -> Sales.SalesOrderHeader.SalesOrderID", header);
        Assert.Contains("- PK_SalesOrderHeader_SalesOrderID (primary key, clustered): SalesOrderID", header);

        Assert.StartsWith("Person.Person (table, ", await Invoke<SqlDescribeTool>(("table", "Person")));   // a bare name: the one schema that has it
        Assert.StartsWith("Person.Person (table, ", await Invoke<SqlDescribeTool>(("table", "[Person].[Person]")));
        Assert.Equal(SqlText.TableNotFound("Nope.Nothing", "aw", "AdventureWorks2022"), await Invoke<SqlDescribeTool>(("table", "Nope.Nothing")));

        string keys = await Invoke<SqlRelationshipsTool>(("table", "Sales.Customer"));
        Assert.Matches(@"^\d+ foreign-key column pairs touching Sales.Customer in aw/AdventureWorks2022 \(join on these\):\n", keys);
        Assert.Contains("- Sales.Customer.PersonID -> Person.Person.BusinessEntityID (FK_Customer_Person_PersonID)", keys);
        Assert.Contains("foreign-key column pairs in aw/AdventureWorks2022", await Invoke<SqlRelationshipsTool>());
    }

    [LiveSqlFact]
    public async Task Indexes_Columns_Descriptions_Constraints_AndTriggers()
    {
        string detail = await Invoke<SqlIndexesTool>(("table", "Sales.SalesOrderDetail"), ("missing", true));
        Assert.StartsWith("3 indexes on Sales.SalesOrderDetail in aw/AdventureWorks2022 (usage since the server started)\n", detail);   // sa reads the DMVs
        Assert.Contains("| Sales.SalesOrderDetail | PK_SalesOrderDetail_SalesOrderID_SalesOrderDetailID | clustered PK | SalesOrderID, SalesOrderDetailID |  |  | ", detail);
        Assert.Contains("| Sales.SalesOrderDetail | AK_SalesOrderDetail_rowguid | nonclustered unique | rowguid |", detail);
        Assert.Contains("\nMissing-index suggestions (the optimizer's hints since the server started, not a design):\n", detail);
        Assert.DoesNotContain("Usage not shown", detail);

        string production = await Invoke<SqlIndexesTool>(("schema", "Production"));
        Assert.Matches(@"^\d+ indexes in schema Production in aw/AdventureWorks2022", production);
        Assert.Contains("| Production.ProductReview | IX_ProductReview_ProductID_Name | nonclustered | ProductID, ReviewerName | Comments |", production);   // an included column
        Assert.Contains("| Production.Document | UQ__Document__", production);
        Assert.Contains(" | nonclustered unique constraint | rowguid |", production);
        Assert.DoesNotContain("Missing-index", production);

        string email = await Invoke<SqlColumnsTool>(("pattern", "EmailAddress"));
        Assert.Matches(@"^\d+ columns in aw/AdventureWorks2022\n", email);
        Assert.Contains("| Person.EmailAddress | table | EmailAddress | nvarchar(50) | yes | E-mail address for the person. |", email);
        Assert.Contains("| Sales.vIndividualCustomer | view | EmailAddress | nvarchar(50) | yes |  |", email);   // an undocumented view column: blank, not NULL
        Assert.Equal("No columns in aw/AdventureWorks2022 named like 'NoSuchColumnAnywhere'", await Invoke<SqlColumnsTool>(("pattern", "NoSuchColumnAnywhere")));

        Assert.Contains("| Person | Person | table | 19972 | Human beings involved with AdventureWorks: employees, customer contacts, and vendor contacts. |", await Invoke<SqlTablesTool>(("schema", "Person")));

        string header = await Invoke<SqlDescribeTool>(("table", "Sales.SalesOrderHeader"));
        Assert.StartsWith("Sales.SalesOrderHeader (table, 26 columns) in aw/AdventureWorks2022\nGeneral sales order information.\n\n| column | type | null | key | default | description |\n", header);
        Assert.Contains("| SalesOrderID | int identity | no | PK |  | Primary key. |", header);
        Assert.Contains("\nCheck constraints:\n", header);
        Assert.Contains("- CK_SalesOrderHeader_Status: ([Status]>=(0) AND [Status]<=(8))", header);
        Assert.EndsWith("\nTriggers:\n- uSalesOrderHeader (after UPDATE)", header);
    }

    [LiveSqlFact]
    public async Task AQuery_BindsItsParameters_CapsItsRows_AndReadsEveryType()
    {
        string smiths = await Invoke<SqlQueryTool>(
            ("sql", "SELECT TOP 3 FirstName, LastName FROM Person.Person WHERE LastName = @last ORDER BY FirstName"),
            ("params", new Dictionary<string, object?> { ["last"] = "Smith" }.ToJsonElement()));
        Assert.StartsWith("3 rows × 2 columns from aw/AdventureWorks2022 (", smiths);
        Assert.Contains("| FirstName | LastName |\n|---|---|\n| Abigail | Smith |", smiths);

        var watch = Stopwatch.StartNew();
        string capped = await Invoke<SqlQueryTool>(("sql", "SELECT d.* FROM Sales.SalesOrderDetail AS d CROSS JOIN Sales.SalesOrderDetail AS e"), ("max_rows", 3));
        Assert.StartsWith("3 rows+ × 11 columns from aw/AdventureWorks2022 (", capped);
        Assert.Contains("— the first 3 shown, more exist", capped);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the cap should stop the server, took {watch.Elapsed}");   // 14 billion rows: never drained

        string types = await Invoke<SqlQueryTool>(("sql",
            "SELECT CAST('café' AS varchar(10)) AS v, CAST(N'naïve' AS nvarchar(10)) AS nv, CAST(12345678901234567890.123456789 AS decimal(38,9)) AS d, " +
            "CAST(99999999999999999999999999999999999999 AS decimal(38,0)) AS big, CAST(0xDEADBEEF AS varbinary(4)) AS b, CAST(1 AS bit) AS bit, " +
            "CAST('2009-01-07T13:05:09.5' AS datetime2(3)) AS dt, CAST(NULL AS int) AS nothing"));
        Assert.Contains("| café | naïve | 12345678901234567890.123456789 | 99999999999999999999999999999999999999 | 0xDEADBEEF | true | 2009-01-07 13:05:09.5 | NULL |", types);

        string clr = await Invoke<SqlQueryTool>(("sql", "SELECT TOP 1 a.SpatialLocation, a.SpatialLocation.ToString() AS wkt FROM Person.Address AS a ORDER BY a.AddressID"));
        Assert.Contains("| (geography: select it with .ToString()) | POINT (", clr);   // the CLR type unread, its text read
        Assert.Contains("| (column 1) | x |\n|---|---|\n| 1 | NULL |", await Invoke<SqlQueryTool>(("sql", "SELECT 1, CAST(NULL AS int) AS x")));   // a column with no name
    }

    [LiveSqlFact]
    public async Task ServerErrors_Timeouts_AndCancels_AreOutcomes_NotCrashes()
    {
        Assert.Equal("Error: the server refused it (aw): Msg 208, line 1: Invalid object name 'dbo.NoSuchTable'.", await Invoke<SqlQueryTool>(("sql", "SELECT * FROM dbo.NoSuchTable")));
        Assert.StartsWith("1 row × 1 column from aw/master (", await Invoke<SqlQueryTool>(("sql", "SELECT DB_NAME() AS db"), ("database", "master")));

        _settings.SqlQueryTimeoutSeconds = 1;
        Assert.Equal(SqlText.Timeout("aw", "1"), await Invoke<SqlQueryTool>(("sql", Heavy)));

        _settings.SqlQueryTimeoutSeconds = 60;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _tools.OfType<SqlQueryTool>().Single().InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["sql"] = Heavy }), cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"the cancel should stop the query, took {watch.Elapsed}");
    }

    [LiveSqlFact]
    public async Task EveryBatch_IsRolledBack_EvenOneThatWrites()
    {
        // The second layer under the gate: the access itself, handed a write (no tool would pass it), leaves nothing behind.
        const string read = "SELECT Name FROM Sales.Currency WHERE CurrencyCode = 'AFA'";
        var before = await _access.RunAsync(null, null, null, read, [], 10, 30, CancellationToken.None);
        var during = await _access.RunAsync(null, null, null, "UPDATE Sales.Currency SET Name = Name + N' (changed)' WHERE CurrencyCode = 'AFA'; " + read, [], 10, 30, CancellationToken.None);
        var after = await _access.RunAsync(null, null, null, read, [], 10, 30, CancellationToken.None);

        Assert.Equal(SqlOutcome.Ok, during.Outcome);
        Assert.Equal(before.Grids[0].Rows[0][0] + " (changed)", during.Grids[0].Rows[0][0]);   // the write happened inside the transaction
        Assert.Equal(before.Grids[0].Rows[0][0], after.Grids[0].Rows[0][0]);                    // and was rolled back
    }

    [LiveSqlFact]
    public async Task SqlCheck_PassesEveryLine()
    {
        // 2026-10-03: --sql-check under the JIT; the published exe is where it earns its keep.
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 400;
        int exit = await SqlCheck.RunAsync(console, new SqlCatalog([new SqlNamedConnection("aw", LiveSql.Config!, "test")], []), "aw", 30, CancellationToken.None);
        Assert.True(exit == 0, console.Output);
        Assert.Contains("SQL CHECK PASS  6 checks", console.Output);   // sql:types passes only with geography read as the unreadable marker
    }

    [Fact]
    public async Task SqlCheck_WithNoSuchConnection_FailsAtOnce()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Width = 400;
        Assert.Equal(1, await SqlCheck.RunAsync(console, SqlCatalog.Empty, "nope", 30, CancellationToken.None));
        Assert.Contains("sql:connection", console.Output);
        Assert.Contains(SqlText.NoConnections, console.Output);
        Assert.Contains("SQL CHECK FAIL  1 of 1 checks failed", console.Output);
    }
}

internal static class JsonElementExtensions
{
    public static System.Text.Json.JsonElement ToJsonElement(this Dictionary<string, object?> values)
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in values)
        {
            node[key] = value switch
            {
                null => null,
                string s => System.Text.Json.Nodes.JsonValue.Create(s),
                long l => System.Text.Json.Nodes.JsonValue.Create(l),
                int i => System.Text.Json.Nodes.JsonValue.Create(i),
                _ => throw new ArgumentException("unsupported"),
            };
        }

        using var document = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
