using System.Globalization;
using Microsoft.Extensions.AI;
using MySqlConnector;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a MySQL or MariaDB server is reachable (2026-09-30): <c>NEONSIDEKICK_TEST_MYSQL_CONNECTION</c>
/// holds a MySqlConnector connection string to it whose user owns its database (the dev containers:
/// <c>Server=127.0.0.1;Port=3306;User ID=neon;Password=…;Database=neon</c>, or port 3307 for MariaDB) — the fixtures (<c>ns_*</c>)
/// are made once, here, through a plain connection, never through the tools. Skipped without; never a CI safety net.
/// </summary>
internal static class LiveMySql
{
    public const string Variable = "NEONSIDEKICK_TEST_MYSQL_CONNECTION";

    public static readonly MySqlConnectionConfig? Config;
    public static readonly string Unavailable = "";
    public static readonly string Database = "";

    private static readonly string[] Fixtures =
    [
        "CREATE TABLE ns_customers (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, email VARCHAR(200) UNIQUE COMMENT 'Where to write', created DATETIME DEFAULT CURRENT_TIMESTAMP) COMMENT='People who buy things'",
        "CREATE TABLE ns_orders (id INT PRIMARY KEY, customer_id INT NOT NULL, total DECIMAL(12,2), notes TEXT, CONSTRAINT ns_total_ck CHECK (total >= 0), CONSTRAINT ns_orders_customer_fk FOREIGN KEY (customer_id) REFERENCES ns_customers(id))",
        "CREATE INDEX ns_orders_total_ix ON ns_orders(total)",
        "CREATE TABLE ns_audit (what VARCHAR(50))",
        "CREATE FUNCTION ns_plain() RETURNS INT MODIFIES SQL DATA NOT DETERMINISTIC BEGIN INSERT INTO ns_audit VALUES ('plain'); RETURN 1; END",
        "INSERT INTO ns_customers (id, name, email) VALUES (1, 'Ada Lovelace', 'ada@example.com'), (2, 'Alan Turing', 'alan@example.com')",
        "INSERT INTO ns_orders VALUES (10, 1, 42.5, 'first'), (11, 2, 7, NULL), (12, 2, 9, NULL)",
    ];

    static LiveMySql()
    {
        string? text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            Unavailable = $"{Variable} is not set.";
            return;
        }

        try
        {
            var builder = new MySqlConnectionStringBuilder(text) { ConnectionTimeout = 5 };
            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            using (var count = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ns_orders'", connection))
            {
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    foreach (string sql in Fixtures)
                    {
                        using var create = new MySqlCommand(sql, connection);
                        create.ExecuteNonQuery();
                    }
                }
            }

            Database = builder.Database;
            Config = new MySqlConnectionConfig
            {
                Host = builder.Server,
                Port = (int)builder.Port,
                Database = builder.Database,
                User = builder.UserID,
                Password = WindowsCredentials.Protect(builder.Password).Value ?? builder.Password,
                ConnectTimeoutSeconds = 5,
            };
        }
        catch (Exception ex) when (ex is MySqlException or ArgumentException or InvalidOperationException or FormatException)
        {
            Unavailable = "No MySQL server (or its user may not create the fixtures): " + ex.Message;
        }
    }

    /// <summary>A plain count through a connection of the test's own, outside the tools.</summary>
    public static int Count(string sql)
    {
        using var connection = new MySqlConnection(Environment.GetEnvironmentVariable(Variable)!);
        connection.Open();
        using var command = new MySqlCommand(sql, connection);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}

/// <summary>Skips unless <see cref="LiveMySql"/> connected: the MySQL tools against a real server.</summary>
public sealed class LiveMySqlFactAttribute : FactAttribute
{
    public LiveMySqlFactAttribute()
    {
        if (LiveMySql.Config is null)
        {
            Skip = LiveMySql.Unavailable;
        }
    }
}

/// <summary>The eight MySQL tools against the fixtures (2026-09-30): what each returns, and every read-only layer under the gate.</summary>
public sealed class LiveMySqlTests
{
    private readonly AppSettingsData _settings = new() { MySqlTools = true };
    private readonly MySqlAccess _access;
    private readonly IReadOnlyList<AIFunction> _tools;

    public LiveMySqlTests()
    {
        var catalog = LiveMySql.Config is { } config ? new MySqlCatalog([new MySqlNamedConnection("live", config, "test")], []) : MySqlCatalog.Empty;
        _access = new MySqlAccess(() => catalog);
        _tools = ChatScreen.MySqlTools(_access, () => _settings);
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private Task<SqlRun> Raw(string sql, int timeoutSeconds = 30, CancellationToken cancellationToken = default) =>
        _access.RunAsync("live", null, null, [sql], [], 10, timeoutSeconds, cancellationToken);

    [LiveMySqlFact]
    public async Task TheCatalogTools_FindDescribeAndJoinTheFixtures()
    {
        string db = LiveMySql.Database;
        Assert.Contains("| " + db + " |", await Invoke<MySqlDatabasesTool>());
        string tables = await Invoke<MySqlTablesTool>(("pattern", "ns_"));
        Assert.Contains("| " + db + " | ns_customers | table |", tables);
        Assert.Contains("People who buy things", tables);

        string described = await Invoke<MySqlDescribeTool>(("table", "ns_orders"));
        Assert.StartsWith(db + ".ns_orders (table, 4 columns) in live", described);
        Assert.Contains("| id | int", described);
        Assert.Contains("| PK |", described);
        Assert.Contains("| total | decimal(12,2) | yes |", described);
        Assert.Contains("- out: " + db + ".ns_orders.customer_id -> " + db + ".ns_customers.id (ns_orders_customer_fk)", described);
        Assert.Contains("ns_orders_total_ix (btree): total", described);
        Assert.Contains("- ns_total_ck: ", described);

        string customers = await Invoke<MySqlDescribeTool>(("table", "NS_CUSTOMERS"));   // a name in any case
        Assert.Contains("Where to write", customers);
        Assert.Contains("- in : " + db + ".ns_orders.customer_id -> " + db + ".ns_customers.id", customers);
        Assert.Equal(MySqlText.TableNotFound("ns_nothing", "live/" + db), await Invoke<MySqlDescribeTool>(("table", "ns_nothing")));
        Assert.Contains(db + ".ns_orders.customer_id -> " + db + ".ns_customers.id", await Invoke<MySqlRelationshipsTool>(("table", "ns_customers")));
        Assert.Contains("| " + db + ".ns_customers | table | email | varchar(200) | yes | Where to write |", await Invoke<MySqlColumnsTool>(("pattern", "email")));
        Assert.Contains("ns_orders_total_ix", await Invoke<MySqlIndexesTool>(("table", "ns_orders")));
    }

    [LiveMySqlFact]
    public async Task TheQueryTool_BindsParams_CapsRows_AndReadsEveryType()
    {
        string one = await Invoke<MySqlQueryTool>(("sql", "SELECT name, email FROM ns_customers WHERE id = @id;"), ("params", System.Text.Json.JsonDocument.Parse("""{"id": 2}""").RootElement.Clone()));
        Assert.Contains("| Alan Turing | alan@example.com |", one);
        Assert.StartsWith("2 rows+ × 1 column", await Invoke<MySqlQueryTool>(("sql", "SELECT id FROM ns_orders ORDER BY id"), ("max_rows", 2)));
        string types = await Invoke<MySqlQueryTool>(("sql", "SELECT CAST('12345678901234567890123456789012345.5' AS DECIMAL(65,30)) AS big, o.notes, o.total FROM ns_orders o WHERE o.id = 10"));
        Assert.Contains("| 12345678901234567890123456789012345.500000000000000000000000000000 | first | 42.50 |", types);
        Assert.StartsWith("Error: the server refused it (live): Error 1146:", await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM ns_no_such_table")));
        Assert.StartsWith("Error: the server refused it (live): ", await Invoke<MySqlQueryTool>(("sql", "SELECT @undefined")));   // no user variables: an unbound @name is an error
    }

    /// <summary>Under the gate, the server's read-only transaction, called straight through <see cref="MySqlAccess"/> as a bypass would reach it: every write refused (ERROR 1792), nothing changed.</summary>
    [LiveMySqlFact]
    public async Task UnderTheGate_TheReadOnlyTransaction_RefusesEveryWrite_AndLeavesNothingChanged()
    {
        int customers = LiveMySql.Count("SELECT COUNT(*) FROM ns_customers");
        int audit = LiveMySql.Count("SELECT COUNT(*) FROM ns_audit");
        foreach (string sql in new[] { "UPDATE ns_customers SET name = 'x'", "DELETE FROM ns_customers", "INSERT INTO ns_audit VALUES ('x')", "SELECT * FROM ns_customers FOR UPDATE", "SELECT ns_plain()", "CREATE TEMPORARY TABLE ns_tmp (x INT)" })
        {
            var run = await Raw(sql);
            Assert.True(run.Outcome == SqlOutcome.Failed, sql + " ran: " + run.Outcome);
            Assert.Contains("1792", run.Detail);
        }

        Assert.Equal(customers, LiveMySql.Count("SELECT COUNT(*) FROM ns_customers WHERE name <> 'x'"));
        Assert.Equal(audit, LiveMySql.Count("SELECT COUNT(*) FROM ns_audit"));

        // The session reads strings as the gate does, even when the server's own mode would not.
        var escaped = await Raw("SELECT 'a\\'; DROP TABLE ns_audit; --' AS s");
        Assert.Equal("a'; DROP TABLE ns_audit; --", escaped.Grids[0].Rows[0][0]);
        Assert.Equal(1, LiveMySql.Count("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ns_audit'"));
    }

    [LiveMySqlFact]
    public async Task Timeouts_AndCancels_AreOutcomes_NotCrashes()
    {
        Assert.Equal(SqlOutcome.Timeout, (await Raw(MySqlCheck.SlowQuery, timeoutSeconds: 1)).Outcome);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Raw(MySqlCheck.SlowQuery, 60, cancel.Token));
        Assert.Equal(SqlOutcome.Ok, (await Raw("SELECT 1")).Outcome);   // the pool hands out a good session afterwards
    }

    [LiveMySqlFact]
    public async Task TheWizardsTest_SaysWhoItIs_AndReadsTheGrants()
    {
        var run = await _access.RunAsync("live", null, null, [MySqlCatalogQueries.WhoAmI, MySqlCatalogQueries.Grants], [], 50, 30, CancellationToken.None);
        Assert.Equal(SqlOutcome.Ok, run.Outcome);
        Assert.Contains("@", run.Grids[0].Rows[0][0]);
        Assert.NotEmpty(MySqlText.WritePowers(run.Grids[1].Rows.Select(r => r[0])));   // the fixtures' owner can write: the wizard warns
    }
}
