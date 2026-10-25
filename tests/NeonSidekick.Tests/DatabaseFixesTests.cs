using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The database review's fixes (2026-10-24, the plan <c>database-tools-review-fixes.md</c>, findings A1–T7): one partial class, a file per
/// phase, each test named for what it holds and citing its finding.
/// </summary>
public sealed partial class DatabaseFixesTests
{
    private static MySqlConnectionConfig Unreachable(string? access = "readwrite") =>
        new() { Host = "127.0.0.1", Port = 1, Database = "sales", User = "u", Password = "p", Access = access, ConnectTimeoutSeconds = 1 };

    private static MySqlAccess Access(MySqlConnectionConfig config) =>
        new(() => new MySqlCatalog([new MySqlNamedConnection("shop", config, "x.json")], []));

    /// <summary>
    /// K7, P8 and P1: the statement is classified once (K7: the audit classified it again, a second parse); the read gate decides whether a
    /// statement the write gate calls a read runs unasked, before any server is asked (P8); and one it refuses (SELECT … FOR UPDATE) is asked
    /// about as a change, not refused outright (P1: it could never reach the pane). Every one still meets the boundary.
    /// </summary>
    [Fact]
    public async Task Execute_ARead_IsClassifiedOnce_AndOneTheReadGateRefuses_IsAskedAsAChange()
    {
        int kinds = 0, outside = 0;
        ServerExecuteHooks Hooks(string? readRefusal) => new(
            (_, _) => null,
            body => { kinds++; return body.StartsWith("SELECT", StringComparison.Ordinal) ? [ServerStatementKind.Read] : [ServerStatementKind.Data]; },
            _ => readRefusal,
            body => body,
            () => (new ExecuteTarget("shop", true, "sales"), null),
            (_, _) => Task.FromResult("read"),
            (_, _) => Task.FromResult(new SqlRun(SqlOutcome.Ok, "", "shop", "sales", [], TimeSpan.Zero) { Changes = 1 }),
            run => "error",
            (_, _) => { outside++; return Task.FromResult<string?>(null); });

        var family = MySqlStatementKinds.Family;
        string[] all = [.. ServerStatementKinds.Names];
        Assert.Equal(ServerWriteText.NoPane(family), await ServerExecute.RunAsync(family, "read-write", all, "SELECT 1", [], 10, 1000, Hooks("refused by the read gate"), null, CancellationToken.None));
        Assert.Equal((1, 1), (kinds, outside));

        (kinds, outside) = (0, 0);
        Assert.Equal("read", await ServerExecute.RunAsync(family, "read-write", all, "SELECT 1", [], 10, 1000, Hooks(null), null, CancellationToken.None));
        Assert.Equal((1, 1), (kinds, outside));

        (kinds, outside) = (0, 0);
        Assert.StartsWith("Changed 1 row", await ServerExecute.RunAsync(family, "read-write", all, "UPDATE t SET a = 1", [], 10, 1000, Hooks(null), (_, _, _, _, _) => Task.FromResult<bool?>(true), CancellationToken.None));
        Assert.Equal((1, 1), (kinds, outside));
    }

    /// <summary>P6: the description sends the model to the family's query tool only while that tool is offered.</summary>
    [Fact]
    public void ExecuteDescription_NamesTheQueryTool_OnlyWhileItIsOffered()
    {
        var settings = new AppSettingsData { MySqlTools = true, MySqlMode = "read-write", MySqlStatementsAllowed = ["data"] };
        var tool = new MySqlExecuteTool(Access(Unreachable()), () => settings, null);
        Assert.Contains("For reading, use mysql_query.", tool.Description);
        settings.ToolsDisabled = [.. settings.ToolsDisabled, MySqlQueryTool.ToolName];
        Assert.DoesNotContain("mysql_query", tool.Description);
        Assert.DoesNotContain("For reading", SqlExecuteTool.DescribeFor([ServerStatementKind.Data], readOffered: false));
    }

    /// <summary>P7: a model's call reads the settings once, so the row cap, the timeout and the result size come from one moment.</summary>
    [Fact]
    public async Task AToolCall_ReadsTheSettingsOnce()
    {
        int reads = 0;
        var settings = new AppSettingsData { MySqlTools = true };
        var tool = new MySqlQueryTool(Access(Unreachable(null)), () => { reads++; return settings; });
        string answer = (string)(await tool.InvokeAsync(new AIFunctionArguments { ["sql"] = "SELECT 1" }))!;
        Assert.StartsWith("Error: could not connect to shop", answer);
        Assert.Equal(1, reads);
    }
}
