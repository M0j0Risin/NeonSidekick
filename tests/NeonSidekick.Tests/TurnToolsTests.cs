using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="ChatScreen.ComposeTurnTools"/> (2026-10-04, out of <c>PrepareTurn</c> for the <c>/botchat</c> bots): the limited
/// list, plan mode's read-only narrowing without <c>present_plan</c>, tools off, and the rules following what is left. The main
/// chat's and headless' behaviour is the existing <c>PrepareTurn</c> tests'.
/// </summary>
public sealed class TurnToolsTests
{
    private sealed class NamedTool(string name) : AIFunction
    {
        public override string Name => name;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("ok");
    }

    private static TurnToolInputs Inputs() => new()
    {
        Standing = [new NamedTool("get_current_time")],
        Web = [new NamedTool("web_search"), new NamedTool("web_fetch")],
        WebEnabled = true,
        Files = [new NamedTool("read_file"), new NamedTool("write_file")],
        FilesEnabled = true,
        Mcp = [new NamedTool("chrome__open"), new NamedTool("chrome__click")],
        McpEnabled = true,
    };

    private static string[] Names(TurnToolSet set) => set.Tools.Select(t => t.Name).ToArray();

    [Fact]
    public void Everything_InTheMainChatsOrder_WithItsRules()
    {
        var set = ChatScreen.ComposeTurnTools(Inputs());

        Assert.Equal(["get_current_time", "read_file", "write_file", "web_search", "web_fetch", "chrome__open", "chrome__click"], Names(set));
        Assert.True(set.Rules.Web);
        Assert.True(set.Rules.Files);
        Assert.True(set.Rules.Mcp);
    }

    [Fact]
    public void OnlyTools_KeepsThoseNames_AndAnEmptiedGroupDropsItsRule()
    {
        var set = ChatScreen.ComposeTurnTools(Inputs() with { OnlyTools = new HashSet<string>(["get_current_time", "chrome__click", "nope"], StringComparer.Ordinal) });

        Assert.Equal(["get_current_time", "chrome__click"], Names(set));
        Assert.False(set.Rules.Web);
        Assert.False(set.Rules.Files);
        Assert.True(set.Rules.Mcp);
        Assert.Equal(["get_current_time"], set.Offered.Select(t => t.Name));
    }

    [Fact]
    public void OnlyTools_NeverOffersAToolTheMainChatDoesNot()
    {
        var set = ChatScreen.ComposeTurnTools(Inputs() with { WebEnabled = false, Disabled = new HashSet<string>(["read_file"], StringComparer.Ordinal), OnlyTools = new HashSet<string>(["web_search", "read_file", "write_file"], StringComparer.Ordinal) });

        Assert.Equal(["write_file"], Names(set));
    }

    [Fact]
    public void PlanReadOnly_Narrows_WithoutPresentPlan()
    {
        var set = ChatScreen.ComposeTurnTools(Inputs() with { PlanReadOnly = true });

        Assert.Contains("read_file", Names(set));
        Assert.DoesNotContain("write_file", Names(set));
        Assert.DoesNotContain(Names(set), n => n.Contains("plan", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolsOff_IsEmpty()
    {
        Assert.Same(TurnToolSet.Empty, ChatScreen.ComposeTurnTools(Inputs() with { ToolsEnabled = false }));
    }

    [Fact]
    public void DisableAllBut_WidensByEveryNameRefused_KeepingTheDisabled()
    {
        var widened = ChatScreen.DisableAllBut(new HashSet<string>(["x"], StringComparer.Ordinal), n => n == "a", [new NamedTool("a"), new NamedTool("b")], null, [new NamedTool("c")]);

        Assert.Equal(["b", "c", "x"], widened.Order(StringComparer.Ordinal));
    }
}
