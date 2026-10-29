using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The eight MySQL tools (2026-09-30) without a server: names, schemas, refusals before any connect, the outcomes of a missing or unreachable connection, the group and its rule. The server's half is <see cref="LiveMySqlTests"/>.</summary>
public sealed class MySqlToolsTests
{
    private readonly AppSettingsData _settings = new() { MySqlTools = true };
    private MySqlCatalog _catalog = MySqlCatalog.Empty;
    private readonly MySqlAccess _access;
    private readonly IReadOnlyList<AIFunction> _tools;

    public MySqlToolsTests()
    {
        _access = new MySqlAccess(() => _catalog);
        _tools = ChatScreen.MySqlTools(_access, () => _settings);
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await Tool<T>().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>A connection to a loopback port nothing listens on: the connect fails fast.</summary>
    private static MySqlNamedConnection Unreachable(string name) =>
        new(name, new MySqlConnectionConfig { Host = "127.0.0.1", Port = 1, Database = "shop", User = "reader", Password = "x", ConnectTimeoutSeconds = 1 }, "test");

    [Fact]
    public void Names_AndSchemas_ArePinned()
    {
        Assert.Equal(MySqlToolNames.WithExecute, _tools.Select(t => t.Name));
        Assert.Equal(MySqlToolNames.WithExecute.Order(StringComparer.Ordinal), ChatScreen.MySqlToolNames.Order(StringComparer.Ordinal));
        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Empty(Properties(Tool<MySqlConnectionsTool>()));
        Assert.Equal(["connection", "pattern"], Properties(Tool<MySqlTablesTool>()));
        Assert.Equal(["table", "connection"], Properties(Tool<MySqlDescribeTool>()));
        Assert.Equal(["connection", "table"], Properties(Tool<MySqlRelationshipsTool>()));
        Assert.Equal(["pattern", "connection"], Properties(Tool<MySqlColumnsTool>()));
        Assert.Equal(["connection", "table"], Properties(Tool<MySqlIndexesTool>()));
        Assert.Equal(["sql", "connection", "params", "max_rows", "to", "sheet", "overwrite"], Properties(Tool<MySqlQueryTool>()));
        Assert.Equal(["sql", "connection", "params", "max_rows"], Properties(Tool<MySqlExecuteTool>()));
        Assert.Contains("LIMIT n", Tool<MySqlQueryTool>().JsonSchema.GetProperty("properties").GetProperty("sql").GetProperty("description").GetString());
    }

    [Fact]
    public async Task WithNoConnection_EveryServerTool_SaysHowToAddOne()
    {
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlConnectionsTool>());
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlTablesTool>());
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlDescribeTool>(("table", "shop.orders")));
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlRelationshipsTool>());
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlColumnsTool>(("pattern", "email")));
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlIndexesTool>());
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlQueryTool>(("sql", "SELECT 1")));
    }

    [Fact]
    public async Task AnUnknownConnection_ListsTheNames_AndAnUnreachableOne_IsAConnectError()
    {
        _catalog = new MySqlCatalog([Unreachable("down"), Unreachable("other")], []);

        Assert.Equal(MySqlText.UnknownConnection("nope", "down, other"), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("connection", "nope")));
        Assert.Equal(MySqlText.ConnectionNeeded("down, other"), await Invoke<MySqlQueryTool>(("sql", "SELECT 1")));   // two, none named (2026-10-09: no default)
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("connection", "down")));
        _catalog = new MySqlCatalog([Unreachable("other")], []);   // the only one when none is named
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlTablesTool>());
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlDescribeTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlIndexesTool>());

        _catalog = new MySqlCatalog([new MySqlNamedConnection("bare", new MySqlConnectionConfig { Host = "127.0.0.1", Port = 1, Database = "shop", User = "u" }, "test")], []);
        Assert.Equal(MySqlText.ConnectFailed("bare", MySqlText.NoPassword("bare")), await Invoke<MySqlQueryTool>(("sql", "SELECT 1")));
    }

    [Fact]
    public async Task TheQueryTool_RefusesWhatTheGateRefuses_AndBadArguments_BeforeAnyConnect()
    {
        _catalog = new MySqlCatalog([Unreachable("down")], []);

        Assert.Equal(MySqlText.NotOneStatement(2), await Invoke<MySqlQueryTool>(("sql", "SELECT 1; DROP TABLE t")));
        Assert.Equal(MySqlText.SelectInto, await Invoke<MySqlQueryTool>(("sql", "SELECT 1 INTO OUTFILE '/tmp/x'")));
        Assert.Equal(MySqlText.ExecutableComment(1, 8), await Invoke<MySqlQueryTool>(("sql", "SELECT /*! 1 */ 1")));
        Assert.Equal(MySqlText.NoSql, await Invoke<MySqlQueryTool>());
        Assert.Equal(SqlText.BadMaxRows(1, 100_000), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("max_rows", 0)));
        Assert.Equal(SqlText.BadMaxRows(1, 100_000), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("max_rows", 100_001)));
        Assert.Contains("1 to 100000", Tool<MySqlQueryTool>().JsonSchema.GetProperty("properties").GetProperty("max_rows").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(MySqlText.BadParams("[1,2]"), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("params", Json("[1,2]"))));
        Assert.Equal(MySqlText.NoTable, await Invoke<MySqlDescribeTool>(("table", " ")));
        Assert.Equal(MySqlText.BadTable("a.b.c"), await Invoke<MySqlDescribeTool>(("table", "a.b.c")));
        Assert.Equal(MySqlText.NoPattern, await Invoke<MySqlColumnsTool>());
    }

    /// <summary>
    /// The lock to the connection's database (2026-10-09): a call that still sends <c>database</c> is refused by every tool; the server's
    /// own databases and a table named in another are refused before any connect, and a statement qualifying nothing but its own database
    /// goes straight on (no names read); any other qualifier sends for the server's names first.
    /// </summary>
    [Fact]
    public async Task EveryTool_KeepsToTheConnectionsDatabase()
    {
        _catalog = new MySqlCatalog([Unreachable("down")], []);

        foreach (var tool in _tools)
        {
            Assert.Equal(MySqlText.NoDatabaseArgument, (string)(await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["sql"] = "SELECT 1", ["table"] = "t", ["pattern"] = "x", ["database"] = "payroll" })))!);
        }

        Assert.Equal(MySqlText.ServerCatalog("information_schema"), await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM information_schema.TABLES")));
        Assert.Equal(MySqlText.ServerCatalog("mysql"), await Invoke<MySqlQueryTool>(("sql", "SELECT user FROM `mysql`.user")));
        Assert.Equal(MySqlText.TableElsewhere("payroll", "shop"), await Invoke<MySqlDescribeTool>(("table", "payroll.salaries")));
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM shop.orders")));
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM payroll.salaries")));   // the names are read first
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlDescribeTool>(("table", "SHOP.orders")));

        // The names come back (2026-10-24, the database review's S3: every leg above ends at "could not connect", which cannot tell
        // whether the boundary ran): another database among them is the boundary's refusal; the own database's goes on to the run.
        _access.Names.Seed([["shop"], ["payroll"]]);
        Assert.Equal(MySqlText.OtherDatabase("payroll", "shop"), await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM payroll.salaries")));
        Assert.Equal(MySqlText.OtherDatabase("payroll", "shop"), await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM t AS payroll WHERE payroll.id = 1")));   // an alias spelled like one
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlQueryTool>(("sql", "SELECT * FROM t AS o WHERE o.id = 1")));   // no other: the run
    }

    [Fact]
    public async Task TheMention_TheHiddenConnections_TheCaps_AndTheGroup()
    {
        var all = new MySqlCatalog([Unreachable("shop"), Unreachable("secret")], []);
        Assert.Equal(["MySQL · 127.0.0.1:1 / shop", "MySQL · 127.0.0.1:1 / shop"], ChatScreen.MySqlChoices(all).Select(i => i.Note));
        _catalog = all.Offered(["shop"]);
        string listing = await Invoke<MySqlConnectionsTool>();
        Assert.Contains("- shop: 127.0.0.1:1 / shop, user reader", listing);
        Assert.EndsWith("\n" + MySqlText.HiddenConnections(1), listing);
        Assert.Equal(100, MySqlQueryTool.DefaultRows(new AppSettingsData()));
        Assert.Equal(600, MySqlTool.TimeoutSeconds(new AppSettingsData { MySqlQueryTimeoutSeconds = 9_999 }));
        Assert.True(ChatScreen.MySqlOffered(_settings, new MySqlAccess(() => all)));
        Assert.False(ChatScreen.MySqlOffered(new AppSettingsData(), new MySqlAccess(() => all)));   // off by default
        Assert.False(ChatScreen.MySqlOffered(_settings, new MySqlAccess(() => all.Offered([]))));
    }

    [Fact]
    public void TheRule_NamesEveryTool_AndRidesOnlyWithTheGroup_AfterOracles()
    {
        Assert.All(MySqlToolNames.All, name => Assert.Contains(name, Assistant.MySqlRule));
        Assert.Contains("never change data", Assistant.MySqlRule);
        Assert.DoesNotContain(Assistant.MySqlRule, Assistant.DefaultRules(markdown: false, tools: true, oracle: true));
        string all = Assistant.DefaultRules(markdown: false, tools: true, sql: true, oracle: true, mysql: true);
        Assert.True(all.IndexOf(Assistant.OracleRule, StringComparison.Ordinal) < all.IndexOf(Assistant.MySqlRule, StringComparison.Ordinal));
        Assert.Contains("mysql_query reads the MySQL databases (not mysql or mariadb)", Assistant.ShellNativeRule(false, false, false, false, mysql: true));
        Assert.All(MySqlToolNames.All, name => Assert.True(NeonSidekick.Plans.PlanTools.Allowed(name), name));
        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, MySqlEnabled: true, MySqlTools: 7);
        Assert.Contains(Assistant.MySqlRule, SystemPromptSummary.SystemPrompt(facts));
        var group = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, mysql: _tools, mysqlEnabled: false), g => g.Label == ToolsText.MySqlTabTitle);
        Assert.Contains(SystemPromptSummary.MySqlOffSuffix, group.Note);
    }
}
