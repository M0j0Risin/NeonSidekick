using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The six SQL tools (2026-09-23) without a server: their names, schemas and wording, the arguments they refuse,
/// the gate in front of <c>sql_query</c>, and the outcomes a missing connection or an unreachable one gives.
/// The server's half is <see cref="LiveSqlTests"/>.
/// </summary>
public sealed class SqlToolsTests
{
    private readonly AppSettingsData _settings = new() { SqlTools = true };   // the switch off by default since 2026-09-29
    private SqlCatalog _catalog = SqlCatalog.Empty;
    private readonly IReadOnlyList<AIFunction> _tools;

    public SqlToolsTests()
    {
        _tools = ChatScreen.SqlTools(new SqlAccess(() => _catalog), () => _settings);
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction => (string)(await Tool<T>().InvokeAsync(Args(pairs)))!;

    /// <summary>A connection to a named pipe nobody serves: the connect fails fast, the way a stopped server does.</summary>
    private static SqlNamedConnection Unreachable(string name) =>
        new(name, new SqlConnectionConfig { Server = @"np:\\.\pipe\neonsidekick-test-" + Guid.NewGuid().ToString("N") + @"\sql\query", Auth = "windows", ConnectTimeoutSeconds = 1, Encrypt = "optional" }, "test");

    [Fact]
    public void Names_Schemas_AndDescriptions_ArePinned()
    {
        Assert.Equal(SqlToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(SqlToolNames.All.Order(StringComparer.Ordinal), ChatScreen.SqlToolNames.Order(StringComparer.Ordinal));
        foreach (var tool in _tools)
        {
            Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }

        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        static string[] Required(AIFunction tool) => tool.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        Assert.Empty(Properties(Tool<SqlConnectionsTool>()));
        Assert.Equal(["connection"], Properties(Tool<SqlDatabasesTool>()));
        Assert.Equal(["connection", "database", "schema", "pattern"], Properties(Tool<SqlTablesTool>()));
        Assert.Equal(["table", "connection", "database"], Properties(Tool<SqlDescribeTool>()));
        Assert.Equal(["table"], Required(Tool<SqlDescribeTool>()));
        Assert.Equal(["connection", "database", "table"], Properties(Tool<SqlRelationshipsTool>()));
        Assert.Equal(["pattern", "connection", "database", "schema"], Properties(Tool<SqlColumnsTool>()));
        Assert.Equal(["pattern"], Required(Tool<SqlColumnsTool>()));
        Assert.Equal(["connection", "database", "table", "schema", "missing"], Properties(Tool<SqlIndexesTool>()));
        Assert.Empty(Required(Tool<SqlIndexesTool>()));
        Assert.Equal("boolean", Tool<SqlIndexesTool>().JsonSchema.GetProperty("properties").GetProperty("missing").GetProperty("type").GetString());
        Assert.Equal(["sql", "connection", "database", "params", "max_rows"], Properties(Tool<SqlQueryTool>()));
        Assert.Equal(["sql"], Required(Tool<SqlQueryTool>()));
        Assert.Equal("object", Tool<SqlQueryTool>().JsonSchema.GetProperty("properties").GetProperty("params").GetProperty("type").GetString());
    }

    [Fact]
    public async Task WithNoConnection_EveryServerTool_SaysHowToAddOne()
    {
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlConnectionsTool>());
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlDatabasesTool>());
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlTablesTool>());
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlDescribeTool>(("table", "Sales.Customer")));
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlRelationshipsTool>());
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlColumnsTool>(("pattern", "Email")));
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlIndexesTool>());
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlIndexesTool>(("table", "Sales.Customer")));
        Assert.Equal(SqlText.NoConnections, await Invoke<SqlQueryTool>(("sql", "SELECT 1")));
    }

    [Fact]
    public async Task AnUnknownConnection_ListsTheNames_AndAnUnreachableOne_IsAConnectError()
    {
        _catalog = new SqlCatalog([Unreachable("down"), Unreachable("other")], []);

        Assert.Equal(SqlText.UnknownConnection("nope", "down, other"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("connection", "nope")));
        string failed = await Invoke<SqlQueryTool>(("sql", "SELECT 1"));
        Assert.StartsWith("Error: could not connect to down: ", failed);
        Assert.Contains("Named Pipes Provider", failed);
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlTablesTool>(("connection", "OTHER")));   // by name, any case
        _settings.SqlDefaultConnection = "other";
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlDatabasesTool>());   // the default when none is named
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlDescribeTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlRelationshipsTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlColumnsTool>(("pattern", "Email")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<SqlIndexesTool>(("schema", "Sales"), ("missing", true)));
    }

    [Fact]
    public async Task ARunAsConnection_SignsInUnderItsToken_AndAMissingPassword_NamesTheFix()
    {
        string target = "NeonSidekick.Tests/" + Guid.NewGuid().ToString("N");
        string pipe = @"np:\\.\pipe\neonsidekick-test-" + Guid.NewGuid().ToString("N") + @"\sql\query";
        var runas = new SqlConnectionConfig { Server = pipe, Auth = "runas", User = @"NEONSIDEKICK-TEST\nobody", PasswordStore = "credman", Credential = target, ConnectTimeoutSeconds = 1, Encrypt = "optional" };
        _catalog = new SqlCatalog([new SqlNamedConnection("prod", runas, "test")], []);
        try
        {
            Assert.Equal(SqlText.ConnectFailed("prod", SqlText.NoCredential(target)), await Invoke<SqlQueryTool>(("sql", "SELECT SUSER_SNAME()")));

            // With the password there, the netonly token is made and the open runs under it: the pipe nobody serves answers as it would for anyone.
            WindowsCredentials.WriteGeneric(target, runas.User!, "x");
            string failed = await Invoke<SqlQueryTool>(("sql", "SELECT SUSER_SNAME()"));
            Assert.StartsWith("Error: could not connect to prod: ", failed);
            Assert.Contains("Named Pipes Provider", failed);
            Assert.Contains("- prod (default): " + pipe + " / (the login's default database), windows sign-in as NEONSIDEKICK-TEST\\nobody (runas)", await Invoke<SqlConnectionsTool>());
        }
        finally
        {
            WindowsCredentials.DeleteGeneric(target);
        }

        _catalog = new SqlCatalog([new SqlNamedConnection("bare", new SqlConnectionConfig { Server = pipe, User = "sa" }, "test")], []);
        Assert.Equal(SqlText.ConnectFailed("bare", SqlText.NoPassword("bare")), await Invoke<SqlQueryTool>(("sql", "SELECT 1")));
    }

    [Fact]
    public async Task TheQueryTool_RefusesWhatTheGateRefuses_AndBadArguments_BeforeAnyConnect()
    {
        _catalog = new SqlCatalog([Unreachable("down")], []);

        Assert.Equal(SqlText.NotOneStatement(2), await Invoke<SqlQueryTool>(("sql", "SELECT 1 DELETE FROM t")));
        Assert.Equal(SqlText.NotASelect("UPDATE"), await Invoke<SqlQueryTool>(("sql", "UPDATE t SET x = 1")));
        Assert.Equal(SqlText.NoSql, await Invoke<SqlQueryTool>());
        Assert.Equal(SqlText.BadMaxRows(1, 1000), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("max_rows", 0)));
        Assert.Equal(SqlText.BadMaxRows(1, 1000), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("max_rows", 1001)));
        Assert.Equal(ClockText.BadInteger("max_rows", "lots"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("max_rows", "lots")));
        Assert.Equal(SqlText.BadParams("[1,2]"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("params", Json("[1,2]"))));
        Assert.Equal(SqlText.BadParams("5"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("params", Json("5"))));
        Assert.Equal(SqlText.BadParams("""[{"a":1},{"b":2}]"""), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("params", Json("""[{"a":1},{"b":2}]"""))));
        Assert.Equal(SqlText.BadParamName("1x"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("params", Json("""{"1x": 1}"""))));
        Assert.Equal(SqlText.BadParamValue("x"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("params", Json("""{"x": [1]}"""))));
        Assert.Equal(SqlText.NoTable, await Invoke<SqlDescribeTool>(("table", "  ")));
        Assert.Equal(SqlText.NoPattern, await Invoke<SqlColumnsTool>());
        Assert.Equal(SqlText.NoPattern, await Invoke<SqlColumnsTool>(("pattern", " ")));
        Assert.Equal(SqlText.BadMissing("maybe"), await Invoke<SqlIndexesTool>(("missing", "maybe")));
    }

    [Fact]
    public void Parameters_AreTypedByTheirValues()
    {
        var read = SqlQueryTool.ReadParameters(Json("""{"@id": 43659, "name": "Ken", "price": 3.25, "big": 1e40, "on": true, "off": false, "none": null}"""), out var error);
        Assert.Null(error);
        Assert.Equal(
            [new SqlParameterValue("id", 43659L), new SqlParameterValue("name", "Ken"), new SqlParameterValue("price", 3.25m), new SqlParameterValue("big", 1e40), new SqlParameterValue("on", true), new SqlParameterValue("off", false), new SqlParameterValue("none", null)],
            read!);

        Assert.Equal(System.Data.SqlDbType.BigInt, SqlAccess.Bind(new("id", 5L)).SqlDbType);
        Assert.Equal("@id", SqlAccess.Bind(new("id", 5L)).ParameterName);
        Assert.Equal(4000, SqlAccess.Bind(new("s", "short")).Size);
        Assert.Equal(-1, SqlAccess.Bind(new("s", new string('x', 4001))).Size);
        Assert.Equal(System.Data.SqlDbType.Decimal, SqlAccess.Bind(new("d", 3.25m)).SqlDbType);
        Assert.Equal(System.Data.SqlDbType.Bit, SqlAccess.Bind(new("b", true)).SqlDbType);
        Assert.Equal(System.Data.SqlDbType.Float, SqlAccess.Bind(new("f", 1e40)).SqlDbType);
        Assert.Equal(DBNull.Value, SqlAccess.Bind(new("n", null)).Value);
        Assert.Equal(System.Data.SqlDbType.Int, SqlAccess.Bind(new("i", 7)).SqlDbType);
    }

    [Fact]
    public void ThePercentMention_ListsEachConnection_WithWhereItPoints()
    {
        var catalog = new SqlCatalog(
            [
                new SqlNamedConnection("aw", new SqlConnectionConfig { Server = "127.0.0.1,1433", Database = "AdventureWorks2022", User = "sa", Description = "the sample" }, "test"),
                new SqlNamedConnection("corp", new SqlConnectionConfig { Server = "corp\\inst", Auth = "windows" }, "test"),
            ],
            []);

        var items = ChatScreen.SqlChoices(catalog);

        Assert.Equal(["aw", "corp"], items.Select(i => i.Text));
        Assert.Equal(["127.0.0.1,1433 / AdventureWorks2022 — the sample", "corp\\inst"], items.Select(i => i.Note));
        Assert.Empty(ChatScreen.SqlChoices(SqlCatalog.Empty));
        Assert.True(new AppSettingsData().SqlPercentMention);
    }

    [Fact]
    public async Task OnlyTheOfferedConnections_AreReachable_AndTheModelIsToldHowManyAreHidden()
    {
        var all = new SqlCatalog([Unreachable("aw"), Unreachable("prod"), Unreachable("secret")], []);
        Assert.Same(all, all.Offered(null));
        var narrowed = all.Offered([" PROD ", "gone"]);
        Assert.Equal(["prod"], narrowed.Connections.Select(c => c.Name));
        Assert.Equal(2, narrowed.Hidden);
        Assert.Empty(all.Offered([]).Connections);
        Assert.Equal(3, all.Offered([]).Hidden);

        _catalog = narrowed;
        string listing = await Invoke<SqlConnectionsTool>();
        Assert.StartsWith("1 SQL connection (", listing);
        Assert.EndsWith("\n" + SqlText.HiddenConnections(2), listing);
        Assert.DoesNotContain("secret", listing);
        Assert.Equal("2 more connections in sql.json are switched off for this profile (the SQL tab of /tools).", SqlText.HiddenConnections(2));
        Assert.Equal("1 more connection in sql.json is switched off for this profile (the SQL tab of /tools).", SqlText.HiddenConnections(1));

        Assert.Equal(SqlText.UnknownConnection("secret", "prod"), await Invoke<SqlQueryTool>(("sql", "SELECT 1"), ("connection", "secret")));
        _settings.SqlDefaultConnection = "secret";   // a hidden default falls to the first offered
        Assert.StartsWith("Error: could not connect to prod: ", await Invoke<SqlQueryTool>(("sql", "SELECT 1")));
        Assert.Equal(["prod"], ChatScreen.SqlChoices(narrowed).Select(i => i.Text));

        var access = new SqlAccess(() => all.Offered([]));
        Assert.False(ChatScreen.SqlOffered(_settings, access));   // nothing offered: no SQL group at all
    }

    [Fact]
    public void TheCaps_ComeFromTheSettings_Clamped()
    {
        Assert.Equal(100, SqlQueryTool.DefaultRows(new AppSettingsData()));
        Assert.Equal(1000, SqlQueryTool.DefaultRows(new AppSettingsData { SqlQueryMaxRows = 99_999 }));
        Assert.Equal(30, SqlTool.TimeoutSeconds(new AppSettingsData()));
        Assert.Equal(1, SqlTool.TimeoutSeconds(new AppSettingsData { SqlQueryTimeoutSeconds = -3 }));
        Assert.Null(SqlTablesTool.LikePattern(" "));
        Assert.Equal("%Order%", SqlTablesTool.LikePattern("Order"));
        Assert.Equal("Sales.%", SqlTablesTool.LikePattern("Sales.*"));
        Assert.Equal("Sales_Order", SqlTablesTool.LikePattern("Sales_Order"));
    }

    [Fact]
    public void TheGroup_IsOffered_OnlyWithTheSwitchOn_AndAConnection()
    {
        var access = new SqlAccess(() => _catalog);
        Assert.False(ChatScreen.SqlOffered(_settings, access));
        _catalog = new SqlCatalog([Unreachable("aw")], []);
        Assert.True(ChatScreen.SqlOffered(_settings, access));
        _settings.SqlTools = false;
        Assert.False(ChatScreen.SqlOffered(_settings, access));
    }

    [Fact]
    public void TheRule_NamesEveryTool_AndRidesOnlyWithTheGroup()
    {
        Assert.All(SqlToolNames.All, name => Assert.Contains(name, Assistant.SqlRule));
        Assert.StartsWith("The SQL tools read SQL Server (T-SQL: TOP, not LIMIT)", Assistant.SqlRule);
        Assert.Contains(" " + Assistant.SqlRule, Assistant.DefaultRules(markdown: false, tools: true, sql: true));
        Assert.DoesNotContain(Assistant.SqlRule, Assistant.DefaultRules(markdown: false, tools: true));
        Assert.DoesNotContain(Assistant.SqlRule, Assistant.DefaultRules(markdown: false, tools: false, sql: true));
    }
}
