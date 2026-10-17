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

    /// <summary>
    /// The shopping tools right after the standing ones, ahead of the file tools, and their sentence straight after the file sentence (2026-10-17,
    /// the user's report: "add milk to my shopping list" now and then went to a file); the opening calls still found among the standing and file
    /// tools alone.
    /// </summary>
    [Fact]
    public void TheShoppingTools_ComeBeforeTheFileTools_AndTheirSentenceAfterTheFileSentence()
    {
        var set = ChatScreen.ComposeTurnTools(Inputs() with { Shopping = [new NamedTool("shopping_add"), new NamedTool("shopping_list")], ShoppingEnabled = true });

        Assert.Equal(["get_current_time", "shopping_add", "shopping_list", "read_file", "write_file", "web_search", "web_fetch", "chrome__open", "chrome__click"], Names(set));
        Assert.Equal(["get_current_time", "read_file", "write_file"], set.Offered.Select(t => t.Name));
        Assert.Contains(Assistant.FileRuleWithoutDelete + " " + Assistant.ShoppingRule, set.Rules.DefaultRules(markdown: false), StringComparison.Ordinal);

        var noFiles = ChatScreen.ComposeTurnTools(Inputs() with { FilesEnabled = false, Shopping = [new NamedTool("shopping_add")], ShoppingEnabled = true });
        Assert.Equal(["get_current_time", "shopping_add", "web_search", "web_fetch", "chrome__open", "chrome__click"], Names(noFiles));
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

    private static readonly IReadOnlyList<AIFunction> Pictures = [new NamedTool("view_image"), new NamedTool("image_info"), new NamedTool("image_edit")];

    /// <summary>
    /// The picture tools (2026-10-10, the user's ask): their own group under Image tools, right after the file tools, the picture
    /// sentence riding with them; File tools neither brings nor holds them back (the user's call).
    /// </summary>
    [Fact]
    public void TheImageTools_RideTheirOwnSwitch_AfterTheFileTools_WhateverFileToolsSays()
    {
        var off = ChatScreen.ComposeTurnTools(Inputs() with { Images = Pictures });
        Assert.DoesNotContain("view_image", Names(off));
        Assert.False(off.Rules.Images);
        Assert.DoesNotContain(Assistant.ImageRule, off.Rules.DefaultRules(false), StringComparison.Ordinal);

        var on = ChatScreen.ComposeTurnTools(Inputs() with { Images = Pictures, ImagesEnabled = true });
        Assert.Equal(["get_current_time", "read_file", "write_file", "view_image", "image_info", "image_edit", "web_search", "web_fetch", "chrome__open", "chrome__click"], Names(on));
        Assert.True(on.Rules.Images);
        Assert.Contains(Assistant.FileRuleWithoutDelete + " " + Assistant.ImageRule, on.Rules.DefaultRules(false), StringComparison.Ordinal);   // no delete tool here
        Assert.Equal(["get_current_time", "read_file", "write_file"], on.Offered.Select(t => t.Name));   // the opening calls are found among the standing and file tools alone

        var alone = ChatScreen.ComposeTurnTools(Inputs() with { FilesEnabled = false, Images = Pictures, ImagesEnabled = true });
        Assert.Equal(["get_current_time", "view_image", "image_info", "image_edit"], Names(alone).Take(4));
        Assert.False(alone.Rules.Files);
        Assert.Contains(Assistant.ImageRule, alone.Rules.DefaultRules(false), StringComparison.Ordinal);
        Assert.DoesNotContain(Assistant.FileRuleWithoutDelete, alone.Rules.DefaultRules(false), StringComparison.Ordinal);

        // Every picture tool switched off on /tools reads as the switch off, the sentence with them.
        var emptied = ChatScreen.ComposeTurnTools(Inputs() with { Images = Pictures, ImagesEnabled = true, Disabled = new HashSet<string>(["view_image", "image_info", "image_edit"], StringComparer.Ordinal) });
        Assert.False(emptied.Rules.Images);
        Assert.DoesNotContain("image_edit", Names(emptied));

        // Plan mode keeps the two that read and drops image_edit, which writes.
        var planning = ChatScreen.ComposeTurnTools(Inputs() with { Images = Pictures, ImagesEnabled = true, PlanReadOnly = true });
        Assert.Contains("view_image", Names(planning));
        Assert.Contains("image_info", Names(planning));
        Assert.DoesNotContain("image_edit", Names(planning));
    }
}
