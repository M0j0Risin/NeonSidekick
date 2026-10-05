using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using Oracle.ManagedDataAccess.Client;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether an Oracle database is reachable (2026-09-30): <c>NEONSIDEKICK_TEST_ORACLE_CONNECTION</c>
/// holds an ODP.NET connection string to it (the dev container, <c>gvenzl/oracle-free</c> with an app user:
/// <c>User Id=neon;Password=…;Data Source=localhost:1521/FREEPDB1</c>) whose user may create tables — the fixtures (<c>NS_*</c>)
/// are made once, here, through a plain connection, never through the tools. Skipped without; never a CI safety net.
/// </summary>
internal static class LiveOracle
{
    public const string Variable = "NEONSIDEKICK_TEST_ORACLE_CONNECTION";

    public static readonly OracleConnectionConfig? Config;
    public static readonly string Unavailable = "";

    /// <summary>The server's major release: the session <c>READ_ONLY</c> layer is there from 23.</summary>
    public static readonly int Release;

    /// <summary>The user the tests sign in as, upper-cased: the schema the fixtures live in.</summary>
    public static readonly string Owner = "";

    private static readonly string[] Fixtures =
    [
        "CREATE TABLE ns_customers (id NUMBER(10) PRIMARY KEY, name VARCHAR2(100 CHAR) NOT NULL, email VARCHAR2(200) UNIQUE, created DATE DEFAULT SYSDATE)",
        "COMMENT ON TABLE ns_customers IS 'People who buy things'",
        "COMMENT ON COLUMN ns_customers.email IS 'Where to write'",
        "CREATE TABLE ns_orders (id NUMBER(10) PRIMARY KEY, customer_id NUMBER(10) NOT NULL REFERENCES ns_customers(id), total NUMBER(12,2) CONSTRAINT ns_total_ck CHECK (total >= 0), notes CLOB, placed TIMESTAMP WITH TIME ZONE)",
        "CREATE INDEX ns_orders_customer_ix ON ns_orders(customer_id)",
        "CREATE TABLE ns_audit (what VARCHAR2(200))",
        "CREATE SEQUENCE ns_seq",
        "CREATE OR REPLACE FUNCTION ns_sneaky RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO ns_audit VALUES ('sneaky'); COMMIT; RETURN 1; END;",
        "CREATE OR REPLACE FUNCTION ns_plain RETURN NUMBER IS BEGIN INSERT INTO ns_audit VALUES ('plain'); RETURN 1; END;",
        "INSERT INTO ns_customers (id, name, email) VALUES (1, 'Ada Lovelace', 'ada@example.com')",
        "INSERT INTO ns_customers (id, name, email) VALUES (2, 'Alan Turing', 'alan@example.com')",
        "INSERT INTO ns_orders (id, customer_id, total, notes) VALUES (10, 1, 42.5, 'first')",
        "INSERT INTO ns_orders (id, customer_id, total) VALUES (11, 2, 7)",
        "INSERT INTO ns_orders (id, customer_id, total) VALUES (12, 2, 9)",
    ];

    static LiveOracle()
    {
        string? text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        try
        {
            var builder = new OracleConnectionStringBuilder(text) { ConnectionTimeout = 5 };
            using var connection = new OracleConnection(builder.ConnectionString);
            connection.Open();
            Release = OracleAccess.Release(connection.ServerVersion);
            using (var count = new OracleCommand("SELECT COUNT(*) FROM user_tables WHERE table_name = 'NS_ORDERS'", connection))
            {
                if (Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                {
                    foreach (string sql in Fixtures)
                    {
                        using var create = new OracleCommand(sql, connection);
                        create.ExecuteNonQuery();
                    }

                    using var commit = new OracleCommand("COMMIT", connection);
                    commit.ExecuteNonQuery();
                }
            }

            Owner = builder.UserID.ToUpperInvariant();
            Config = new OracleConnectionConfig
            {
                DataSource = builder.DataSource,
                User = builder.UserID,
                Password = WindowsCredentials.Protect(builder.Password).Value ?? builder.Password,   // kept as oracle.json keeps it, so every call decrypts
                ConnectTimeoutSeconds = 5,
            };
        }
        catch (Exception ex) when (ex is OracleException or ArgumentException or InvalidOperationException or FormatException)
        {
            Unavailable = "No Oracle database (or its user may not create the fixtures): " + ex.Message;
        }
    }

    /// <summary>A plain count through a connection of the test's own, outside the tools: what the read-only layers must leave unchanged.</summary>
    public static int Count(string sql)
    {
        var builder = new OracleConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)!);
        using var connection = new OracleConnection(builder.ConnectionString);
        connection.Open();
        using var command = new OracleCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Skips unless <see cref="LiveOracle"/> connected: the Oracle tools against a real database.</summary>
public sealed class LiveOracleFactAttribute : FactAttribute
{
    public LiveOracleFactAttribute()
    {
        if (LiveOracle.Config is null)
        {
            Skip = LiveOracle.Unavailable;
        }
    }
}

/// <summary>The eight Oracle tools against the fixtures (2026-09-30): what each returns, and every read-only layer under the gate.</summary>
public sealed class LiveOracleTests
{
    private readonly AppSettingsData _settings = new() { OracleTools = true };
    private readonly OracleAccess _access;
    private readonly IReadOnlyList<AIFunction> _tools;

    public LiveOracleTests()
    {
        var catalog = LiveOracle.Config is { } config ? new OracleCatalog([new OracleNamedConnection("free", config, "test")], []) : OracleCatalog.Empty;
        _access = new OracleAccess(() => catalog);
        _tools = ChatScreen.OracleTools(_access, () => _settings);
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private Task<SqlRun> Raw(string sql, int timeoutSeconds = 30, CancellationToken cancellationToken = default) =>
        _access.RunAsync("free", null, null, [sql], [], 10, timeoutSeconds, cancellationToken);

    [LiveOracleFact]
    public async Task TheCatalogTools_FindDescribeAndJoinTheFixtures()
    {
        Assert.Contains("| " + LiveOracle.Owner + " |", await Invoke<OracleSchemasTool>());
        Assert.Contains("your own", await Invoke<OracleSchemasTool>());

        string tables = await Invoke<OracleTablesTool>(("pattern", "ns_"), ("schema", LiveOracle.Owner.ToLowerInvariant()));
        Assert.Contains("| " + LiveOracle.Owner + " | NS_CUSTOMERS | table |", tables);
        Assert.Contains("People who buy things", tables);

        string described = await Invoke<OracleDescribeTool>(("table", "ns_orders"));
        Assert.StartsWith(LiveOracle.Owner + ".NS_ORDERS (table, 5 columns) in free", described);
        Assert.Contains("| ID | NUMBER(10) | no | PK |", described);
        Assert.Contains("| TOTAL | NUMBER(12,2) | yes |", described);
        Assert.Contains("- out: " + LiveOracle.Owner + ".NS_ORDERS.CUSTOMER_ID -> " + LiveOracle.Owner + ".NS_CUSTOMERS.ID", described);
        Assert.Contains("NS_ORDERS_CUSTOMER_IX (normal): CUSTOMER_ID", described);
        Assert.Contains("- NS_TOTAL_CK: total >= 0", described);
        Assert.DoesNotContain("IS NOT NULL", described);

        string customers = await Invoke<OracleDescribeTool>(("table", LiveOracle.Owner + ".NS_CUSTOMERS"));
        Assert.Contains("| NAME | VARCHAR2(100 CHAR) | no |", customers);
        Assert.Contains("| CREATED | DATE | yes |  | SYSDATE", customers);   // a LONG default, read whole
        Assert.Contains("Where to write", customers);
        Assert.Contains("- in : " + LiveOracle.Owner + ".NS_ORDERS.CUSTOMER_ID -> " + LiveOracle.Owner + ".NS_CUSTOMERS.ID", customers);

        Assert.Equal(OracleText.TableNotFound("ns_nothing", "free", LiveOracle.Owner), await Invoke<OracleDescribeTool>(("table", "ns_nothing")));
        Assert.Contains(LiveOracle.Owner + ".NS_ORDERS.CUSTOMER_ID -> " + LiveOracle.Owner + ".NS_CUSTOMERS.ID", await Invoke<OracleRelationshipsTool>(("table", "ns_customers")));
        Assert.Contains("| " + LiveOracle.Owner + ".NS_CUSTOMERS | table | EMAIL | VARCHAR2(200) | yes | Where to write |", await Invoke<OracleColumnsTool>(("pattern", "email"), ("schema", LiveOracle.Owner)));
        string indexes = await Invoke<OracleIndexesTool>(("table", "ns_orders"));
        Assert.StartsWith("2 indexes on " + LiveOracle.Owner + ".NS_ORDERS in free", indexes);
        Assert.Contains("NS_ORDERS_CUSTOMER_IX", indexes);
    }

    [LiveOracleFact]
    public async Task TheQueryTool_BindsParams_CapsRows_AndReadsEveryType()
    {
        string one = await Invoke<OracleQueryTool>(("sql", "SELECT name, email FROM ns_customers WHERE id = :id;"), ("params", System.Text.Json.JsonDocument.Parse("""{"id": 2}""").RootElement.Clone()));
        Assert.StartsWith("1 row × 2 columns from free/" + LiveOracle.Owner, one);
        Assert.Contains("| Alan Turing | alan@example.com |", one);

        string capped = await Invoke<OracleQueryTool>(("sql", "SELECT id FROM ns_orders ORDER BY id"), ("max_rows", 2));
        Assert.StartsWith("2 rows+ × 1 column", capped);

        string types = await Invoke<OracleQueryTool>(("sql", "SELECT 12345678901234567890123456789012345678 AS big, o.notes, o.total FROM ns_orders o WHERE o.id = 10"));
        Assert.Contains("| 12345678901234567890123456789012345678 | first | 42.5 |", types);

        Assert.StartsWith("Error: the server refused the SQL (free): ORA-00942", await Invoke<OracleQueryTool>(("sql", "SELECT * FROM ns_no_such_table")));
    }

    /// <summary>
    /// Under the gate, the server's own layers, called straight through <see cref="OracleAccess"/> as a bypass would reach them:
    /// DML, a row lock and a function that writes are refused; on 23ai the session layer stops an autonomous transaction and
    /// DDL too (before 23ai DDL would commit, so it is not tried there). Nothing is left changed, whatever ran.
    /// </summary>
    [LiveOracleFact]
    public async Task UnderTheGate_TheServersLayers_RefuseEveryWrite_AndLeaveNothingChanged()
    {
        int customers = LiveOracle.Count("SELECT COUNT(*) FROM ns_customers");
        int audit = LiveOracle.Count("SELECT COUNT(*) FROM ns_audit");

        foreach (string sql in new[] { "UPDATE ns_customers SET name = 'x'", "DELETE FROM ns_customers", "INSERT INTO ns_audit VALUES ('x')", "SELECT * FROM ns_customers FOR UPDATE", "SELECT ns_plain() FROM dual" })
        {
            var run = await Raw(sql);
            Assert.True(run.Outcome == SqlOutcome.Failed, sql + " ran: " + run.Outcome);
            Assert.Matches("ORA-(28193|01456|14551)", run.Detail);
        }

        if (LiveOracle.Release >= OracleAccess.SessionReadOnlyRelease)
        {
            foreach (string sql in new[] { "SELECT ns_sneaky() FROM dual", "CREATE TABLE ns_should_not_exist (x NUMBER)", "WITH FUNCTION f RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO ns_audit VALUES ('with'); COMMIT; RETURN 1; END; SELECT f FROM dual" })
            {
                var run = await Raw(sql);
                Assert.True(run.Outcome == SqlOutcome.Failed, sql + " ran: " + run.Outcome);
                Assert.Contains("ORA-28193", run.Detail);
            }

            Assert.Equal(0, LiveOracle.Count("SELECT COUNT(*) FROM user_tables WHERE table_name = 'NS_SHOULD_NOT_EXIST'"));
        }

        Assert.Equal(customers, LiveOracle.Count("SELECT COUNT(*) FROM ns_customers WHERE name <> 'x'"));
        Assert.Equal(audit, LiveOracle.Count("SELECT COUNT(*) FROM ns_audit"));

        // The gate stands in front of what the session layer lets through: a sequence moves on and no rollback moves it back.
        Assert.StartsWith("Error: the SELECT uses NEXTVAL", await Invoke<OracleQueryTool>(("sql", "SELECT ns_seq.NEXTVAL FROM dual")));
    }

    [LiveOracleFact]
    public async Task Timeouts_AndCancels_AreOutcomes_NotCrashes()
    {
        var timedOut = await Raw(OracleCheck.SlowQuery, timeoutSeconds: 1);
        Assert.Equal(SqlOutcome.Timeout, timedOut.Outcome);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Raw(OracleCheck.SlowQuery, 60, cancel.Token));

        // The pooled session is fine afterwards, and still read-only.
        Assert.Equal(SqlOutcome.Ok, (await Raw("SELECT 1 FROM dual")).Outcome);
        Assert.Equal(SqlOutcome.Failed, (await Raw("UPDATE ns_customers SET name = name")).Outcome);
    }

    [LiveOracleFact]
    public async Task TheWizardsTest_SaysWhoItIs_AndWarnsOfAnAccountThatCanWrite()
    {
        var run = await _access.RunAsync("free", null, null, [OracleCatalogQueries.WhoAmI, OracleCatalogQueries.WritePowers], [], 50, 30, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Equal(LiveOracle.Owner, run.Grids[0].Rows[0][0]);
        Assert.StartsWith(LiveOracle.Release.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", run.Grids[0].Rows[0][2]);
        var powers = run.Grids[1].Rows.Select(r => r[0]).ToList();
        Assert.Contains("CREATE TABLE", powers);   // the fixtures' owner: the warning the wizard shows
        Assert.Contains(powers, p => p.StartsWith("owns ", StringComparison.Ordinal));
    }
}
