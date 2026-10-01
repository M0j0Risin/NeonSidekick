using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Oracle;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The eight Oracle tools (2026-09-30) without a server: their names, schemas and wording, the arguments they refuse, the
/// gate in front of <c>oracle_query</c>, and the outcomes a missing connection or an unreachable one gives. The server's
/// half is <see cref="LiveOracleTests"/>.
/// </summary>
public sealed class OracleToolsTests
{
    private readonly AppSettingsData _settings = new() { OracleTools = true };
    private OracleCatalog _catalog = OracleCatalog.Empty;
    private readonly IReadOnlyList<AIFunction> _tools;

    public OracleToolsTests()
    {
        _tools = ChatScreen.OracleTools(new OracleAccess(() => _catalog), () => _settings);
    }

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction => (string)(await Tool<T>().InvokeAsync(Args(pairs)))!;

    /// <summary>A connection to a loopback port nothing listens on: the connect fails fast, the way a stopped listener does.</summary>
    private static OracleNamedConnection Unreachable(string name) =>
        new(name, new OracleConnectionConfig { DataSource = "127.0.0.1:1/neonsidekick_test", User = "reader", Password = "x", ConnectTimeoutSeconds = 1 }, "test");

    [Fact]
    public void Names_Schemas_AndDescriptions_ArePinned()
    {
        Assert.Equal(OracleToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(OracleToolNames.All.Order(StringComparer.Ordinal), ChatScreen.OracleToolNames.Order(StringComparer.Ordinal));
        foreach (var tool in _tools)
        {
            Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }

        static string[] Properties(AIFunction tool) => tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        static string[] Required(AIFunction tool) => tool.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        Assert.Empty(Properties(Tool<OracleConnectionsTool>()));
        Assert.Equal(["connection"], Properties(Tool<OracleSchemasTool>()));
        Assert.Equal(["connection", "schema", "pattern"], Properties(Tool<OracleTablesTool>()));
        Assert.Equal(["table", "connection", "schema"], Properties(Tool<OracleDescribeTool>()));
        Assert.Equal(["table"], Required(Tool<OracleDescribeTool>()));
        Assert.Equal(["connection", "schema", "table"], Properties(Tool<OracleRelationshipsTool>()));
        Assert.Equal(["pattern", "connection", "schema"], Properties(Tool<OracleColumnsTool>()));
        Assert.Equal(["pattern"], Required(Tool<OracleColumnsTool>()));
        Assert.Equal(["connection", "table", "schema"], Properties(Tool<OracleIndexesTool>()));
        Assert.Equal(["sql", "connection", "schema", "params", "max_rows"], Properties(Tool<OracleQueryTool>()));
        Assert.Equal(["sql"], Required(Tool<OracleQueryTool>()));
        Assert.Contains("FETCH FIRST", Tool<OracleQueryTool>().JsonSchema.GetProperty("properties").GetProperty("sql").GetProperty("description").GetString());
    }

    [Fact]
    public async Task WithNoConnection_EveryServerTool_SaysHowToAddOne()
    {
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleConnectionsTool>());
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleSchemasTool>());
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleTablesTool>());
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleDescribeTool>(("table", "hr.employees")));
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleRelationshipsTool>());
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleColumnsTool>(("pattern", "email")));
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleIndexesTool>());
        Assert.Equal(OracleText.NoConnections, await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual")));
    }

    [Fact]
    public async Task AnUnknownConnection_ListsTheNames_AndAnUnreachableOne_IsAConnectError()
    {
        _catalog = new OracleCatalog([Unreachable("down"), Unreachable("other")], []);

        Assert.Equal(OracleText.UnknownConnection("nope", "down, other"), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("connection", "nope")));
        string failed = await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"));
        Assert.StartsWith("Error: could not connect to down: ORA-", failed);
        Assert.DoesNotContain("\n", failed);
        Assert.DoesNotContain("https://", failed);   // the help link dropped
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleTablesTool>(("connection", "OTHER")));   // by name, any case
        _settings.OracleDefaultConnection = "other";
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleSchemasTool>());   // the default when none is named
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleDescribeTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleRelationshipsTool>(("table", "t")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleColumnsTool>(("pattern", "email")));
        Assert.StartsWith("Error: could not connect to other: ", await Invoke<OracleIndexesTool>(("schema", "hr")));

        _catalog = new OracleCatalog([new OracleNamedConnection("bare", new OracleConnectionConfig { DataSource = "127.0.0.1:1/x", User = "u" }, "test")], []);
        Assert.Equal(OracleText.ConnectFailed("bare", OracleText.NoPassword("bare")), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual")));
    }

    [Fact]
    public async Task TheQueryTool_RefusesWhatTheGateRefuses_AndBadArguments_BeforeAnyConnect()
    {
        _catalog = new OracleCatalog([Unreachable("down")], []);

        Assert.Equal(OracleText.NotOneStatement(2), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual; DELETE FROM t")));
        Assert.Equal(OracleText.NotASelect("UPDATE"), await Invoke<OracleQueryTool>(("sql", "UPDATE t SET x = 1")));
        Assert.Equal(OracleText.Forbidden("FOR UPDATE (it locks rows)"), await Invoke<OracleQueryTool>(("sql", "SELECT * FROM t FOR UPDATE")));
        Assert.Equal(OracleText.NoSql, await Invoke<OracleQueryTool>());
        Assert.Equal(SqlText.BadMaxRows(1, 1000), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("max_rows", 0)));
        Assert.Equal(ClockText.BadInteger("max_rows", "lots"), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("max_rows", "lots")));
        Assert.Equal(OracleText.BadParams("[1,2]"), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("params", Json("[1,2]"))));
        Assert.Equal("Error: '1x' is no parameter name; use letters, digits and _ (bound as :name)", await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("params", Json("""{"1x": 1}"""))));
        Assert.Equal(OracleText.BadSchema("not a name"), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("schema", "not a name")));
        Assert.Equal(OracleText.BadSchema("x;y"), await Invoke<OracleTablesTool>(("schema", "x;y")));
        Assert.Equal(OracleText.NoTable, await Invoke<OracleDescribeTool>(("table", "  ")));
        Assert.Equal(OracleText.BadTable("a.b.c"), await Invoke<OracleDescribeTool>(("table", "a.b.c")));
        Assert.Equal(OracleText.NoPattern, await Invoke<OracleColumnsTool>());
    }

    [Fact]
    public void ThePercentMention_ListsEachConnection_MarkedOracle()
    {
        var catalog = new OracleCatalog(
            [
                new OracleNamedConnection("free", new OracleConnectionConfig { DataSource = "localhost:1521/FREEPDB1", User = "neon", Schema = "HR", Description = "the container" }, "test"),
                new OracleNamedConnection("ledger", new OracleConnectionConfig { DataSource = "db:1521/LEDGER", User = "ro" }, "test"),
            ],
            []);

        var items = ChatScreen.OracleChoices(catalog);

        Assert.Equal(["free", "ledger"], items.Select(i => i.Text));
        Assert.Equal(["Oracle · localhost:1521/FREEPDB1 / HR — the container", "Oracle · db:1521/LEDGER"], items.Select(i => i.Note));
        Assert.Empty(ChatScreen.OracleChoices(OracleCatalog.Empty));
        Assert.True(new AppSettingsData().OraclePercentMention);
        Assert.False(new AppSettingsData().OracleTools);   // off by default, as SQL tools is
    }

    [Fact]
    public async Task OnlyTheOfferedConnections_AreReachable_AndTheModelIsToldHowManyAreHidden()
    {
        var all = new OracleCatalog([Unreachable("free"), Unreachable("prod"), Unreachable("secret")], []);
        _catalog = all.Offered([" PROD "]);

        string listing = await Invoke<OracleConnectionsTool>();
        Assert.StartsWith("1 Oracle connection (", listing);
        Assert.Contains("- prod (default): 127.0.0.1:1/neonsidekick_test, user reader", listing);
        Assert.DoesNotContain("x", listing.Split('\n')[1].Replace("neonsidekick_test", "", StringComparison.Ordinal));   // never the password
        Assert.EndsWith("\n" + OracleText.HiddenConnections(2), listing);
        Assert.DoesNotContain("secret", listing);
        Assert.Equal(OracleText.UnknownConnection("secret", "prod"), await Invoke<OracleQueryTool>(("sql", "SELECT 1 FROM dual"), ("connection", "secret")));
        Assert.False(ChatScreen.OracleOffered(_settings, new OracleAccess(() => all.Offered([]))));
    }

    [Fact]
    public void TheCaps_ComeFromTheSettings_Clamped()
    {
        Assert.Equal(100, OracleQueryTool.DefaultRows(new AppSettingsData()));
        Assert.Equal(1000, OracleQueryTool.DefaultRows(new AppSettingsData { OracleQueryMaxRows = 99_999 }));
        Assert.Equal(30, OracleTool.TimeoutSeconds(new AppSettingsData()));
        Assert.Equal(600, OracleTool.TimeoutSeconds(new AppSettingsData { OracleQueryTimeoutSeconds = 9_999 }));
        Assert.Null(OracleTablesTool.LikePattern(" "));
        Assert.Equal("%NS_%", OracleTablesTool.LikePattern("NS_"));   // an underscore alone is a name's, not a wildcard
        Assert.Equal("HR.%", OracleTablesTool.LikePattern("HR.*"));
        Assert.Equal("EMP%", OracleTablesTool.LikePattern("EMP%"));
    }

    [Fact]
    public void TheGroup_IsOffered_OnlyWithTheSwitchOn_AndAConnection()
    {
        var access = new OracleAccess(() => _catalog);
        Assert.False(ChatScreen.OracleOffered(_settings, access));
        _catalog = new OracleCatalog([Unreachable("free")], []);
        Assert.True(ChatScreen.OracleOffered(_settings, access));
        _settings.OracleTools = false;
        Assert.False(ChatScreen.OracleOffered(_settings, access));
    }

    [Fact]
    public void TheRule_NamesEveryTool_SaysItOnlyReads_AndRidesOnlyWithTheGroup()
    {
        Assert.All(OracleToolNames.All, name => Assert.Contains(name, Assistant.OracleRule));
        Assert.StartsWith("The Oracle tools read Oracle databases (Oracle SQL: FETCH FIRST n ROWS ONLY, not TOP or LIMIT;", Assistant.OracleRule);
        Assert.Contains("never change data", Assistant.OracleRule);
        Assert.Contains(" " + Assistant.OracleRule, Assistant.DefaultRules(markdown: false, tools: true, oracle: true));
        Assert.DoesNotContain(Assistant.OracleRule, Assistant.DefaultRules(markdown: false, tools: true, sql: true));
        Assert.DoesNotContain(Assistant.OracleRule, Assistant.DefaultRules(markdown: false, tools: false, oracle: true));
        // After the SQL sentence when both groups ride.
        string both = Assistant.DefaultRules(markdown: false, tools: true, sql: true, oracle: true);
        Assert.True(both.IndexOf(Assistant.SqlRule, StringComparison.Ordinal) < both.IndexOf(Assistant.OracleRule, StringComparison.Ordinal));
        Assert.Contains("oracle_query reads the Oracle databases (not sqlplus)", Assistant.ShellNativeRule(files: false, git: false, web: false, sql: false, oracle: true));
    }

    [Fact]
    public void ThePrepareTurn_OffersTheGroup_WithItsRule_OnlyWhenOffered()
    {
        _catalog = new OracleCatalog([Unreachable("free")], []);
        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, OracleEnabled: true, OracleTools: 8);
        Assert.True(facts.Oracle);
        Assert.Contains(Assistant.OracleRule, SystemPromptSummary.SystemPrompt(facts));
        Assert.False((facts with { OracleTools = 0 }).Oracle);
        Assert.False((facts with { ToolsEnabled = false }).Oracle);

        var groups = SystemPromptSummary.ToolGroups([], [], [], [], false, oracle: _tools, oracleEnabled: false);
        var group = Assert.Single(groups, g => g.Label == ToolsText.OracleTabTitle);
        Assert.Equal(8, group.Tools.Count);
        Assert.False(group.Offered);
        Assert.Contains(SystemPromptSummary.OracleOffSuffix, group.Note);
    }

    [Fact]
    public void Plans_MayRunEveryOracleTool_TheyAllOnlyRead() =>
        Assert.All(OracleToolNames.All, name => Assert.True(NeonSidekick.Plans.PlanTools.Allowed(name), name));
}
