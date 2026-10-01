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
    private readonly IReadOnlyList<AIFunction> _tools;

    public MySqlToolsTests()
    {
        _tools = ChatScreen.MySqlTools(new MySqlAccess(() => _catalog), () => _settings);
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
        new(name, new MySqlConnectionConfig { Host = "127.0.0.1", Port = 1, User = "reader", Password = "x", ConnectTimeoutSeconds = 1 }, "test");

    [Fact]
    public void Names_AndSchemas_ArePinned()
    {
        Assert.Equal(MySqlToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(MySqlToolNames.All.Order(StringComparer.Ordinal), ChatScreen.MySqlToolNames.Order(StringComparer.Ordinal));
        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Empty(Properties(Tool<MySqlConnectionsTool>()));
        Assert.Equal(["connection"], Properties(Tool<MySqlDatabasesTool>()));
        Assert.Equal(["connection", "database", "pattern"], Properties(Tool<MySqlTablesTool>()));
        Assert.Equal(["table", "connection", "database"], Properties(Tool<MySqlDescribeTool>()));
        Assert.Equal(["connection", "database", "table"], Properties(Tool<MySqlRelationshipsTool>()));
        Assert.Equal(["pattern", "connection", "database"], Properties(Tool<MySqlColumnsTool>()));
        Assert.Equal(["connection", "database", "table"], Properties(Tool<MySqlIndexesTool>()));
        Assert.Equal(["sql", "connection", "database", "params", "max_rows"], Properties(Tool<MySqlQueryTool>()));
        Assert.Contains("LIMIT n", Tool<MySqlQueryTool>().JsonSchema.GetProperty("properties").GetProperty("sql").GetProperty("description").GetString());
    }

    [Fact]
    public async Task WithNoConnection_EveryServerTool_SaysHowToAddOne()
    {
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlConnectionsTool>());
        Assert.Equal(MySqlText.NoConnections, await Invoke<MySqlDatabasesTool>());
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
        Assert.StartsWith("Error: could not connect to down: ", await Invoke<MySqlQueryTool>(("sql", "SELECT 1")));
        _settings.MySqlDefaultConnection = "other";
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlDatabasesTool>());
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlDescribeTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<MySqlIndexesTool>(("database", "shop")));

        _catalog = new MySqlCatalog([new MySqlNamedConnection("bare", new MySqlConnectionConfig { Host = "127.0.0.1", Port = 1, User = "u" }, "test")], []);
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
        Assert.Equal(SqlText.BadMaxRows(1, 1000), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("max_rows", 0)));
        Assert.Equal(MySqlText.BadParams("[1,2]"), await Invoke<MySqlQueryTool>(("sql", "SELECT 1"), ("params", Json("[1,2]"))));
        Assert.Equal(MySqlText.NoTable, await Invoke<MySqlDescribeTool>(("table", " ")));
        Assert.Equal(MySqlText.BadTable("a.b.c"), await Invoke<MySqlDescribeTool>(("table", "a.b.c")));
        Assert.Equal(MySqlText.NoPattern, await Invoke<MySqlColumnsTool>());
    }

    [Fact]
    public async Task TheMention_TheHiddenConnections_TheCaps_AndTheGroup()
    {
        var all = new MySqlCatalog([Unreachable("shop"), Unreachable("secret")], []);
        Assert.Equal(["MySQL · 127.0.0.1:1", "MySQL · 127.0.0.1:1"], ChatScreen.MySqlChoices(all).Select(i => i.Note));
        _catalog = all.Offered(["shop"]);
        string listing = await Invoke<MySqlConnectionsTool>();
        Assert.Contains("- shop (default): 127.0.0.1:1, user reader", listing);
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
        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, MySqlEnabled: true, MySqlTools: 8);
        Assert.Contains(Assistant.MySqlRule, SystemPromptSummary.SystemPrompt(facts));
        var group = Assert.Single(SystemPromptSummary.ToolGroups([], [], [], [], false, mysql: _tools, mysqlEnabled: false), g => g.Label == ToolsText.MySqlTabTitle);
        Assert.Contains(SystemPromptSummary.MySqlOffSuffix, group.Note);
    }
}
