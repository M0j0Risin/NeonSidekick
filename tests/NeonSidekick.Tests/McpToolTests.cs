using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;

namespace NeonSidekick.Tests;

/// <summary>The naming rule (2026-09-20).</summary>
public class McpToolNameTests
{
    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("__", McpToolName.Separator);
        Assert.Equal(64, McpToolName.MaxLength);
    }

    [Theory]
    [InlineData("docker", "docker")]
    [InlineData("my server", "my_server")]
    [InlineData("a.b/c:d", "a_b_c_d")]
    [InlineData("ok-name_1", "ok-name_1")]
    [InlineData("", "_")]
    [InlineData("héllo", "h_llo")]
    public void Sanitize_KeepsLettersDigitsUnderscoreDash_TheRestUnderscore(string text, string expected) =>
        Assert.Equal(expected, McpToolName.Sanitize(text));

    [Fact]
    public void Prefixed_JoinsBothHalvesSanitised_AndCutsAt64()
    {
        Assert.Equal("docker__fetch", McpToolName.Prefixed("docker", "fetch"));
        Assert.Equal("my_server__get_time", McpToolName.Prefixed("my server", "get.time"));
        string cut = McpToolName.Prefixed(new string('s', 40), new string('t', 40));
        Assert.Equal(64, cut.Length);
        Assert.StartsWith(new string('s', 40) + "__", cut);
    }

    [Fact]
    public void Unique_SuffixesRepeats_KeepingTheCap()
    {
        Assert.Equal(["a__x", "a__y", "a__x_2", "a__x_3"], McpToolName.Unique(["a__x", "a__y", "a__x", "a__x"]));
        string long1 = new string('n', 64);
        var unique = McpToolName.Unique([long1, long1]);
        Assert.Equal(long1, unique[0]);
        Assert.Equal(64, unique[1].Length);
        Assert.EndsWith("_2", unique[1]);
        Assert.Empty(McpToolName.Unique([]));
    }
}

/// <summary>The adapter (2026-09-20): the arguments, the render, and a call through the in-process server over the real client.</summary>
public class McpToolTests
{
    [Fact]
    public void Arguments_KeepsAJsonElement_AndWritesClrValuesByHand()
    {
        using var doc = JsonDocument.Parse("""{"n": [1, 2], "s": "x"}""");
        var args = McpTool.Arguments(new Dictionary<string, object?>
        {
            ["element"] = doc.RootElement.GetProperty("n"),
            ["text"] = "hi",
            ["flag"] = true,
            ["count"] = 3,
            ["big"] = 5_000_000_000L,
            ["ratio"] = 1.5,
            ["nothing"] = null,
            ["other"] = new Uri("http://x/"),
        });

        Assert.Equal("[1, 2]", args["element"].GetRawText());
        Assert.Equal("hi", args["text"].GetString());
        Assert.True(args["flag"].GetBoolean());
        Assert.Equal(3, args["count"].GetInt32());
        Assert.Equal(5_000_000_000L, args["big"].GetInt64());
        Assert.Equal(1.5, args["ratio"].GetDouble());
        Assert.Equal(JsonValueKind.Null, args["nothing"].ValueKind);
        Assert.Equal("http://x/", args["other"].GetString());
        doc.Dispose();
        Assert.Equal("[1, 2]", args["element"].GetRawText());   // a clone, not a view into the disposed document
    }

    [Fact]
    public void Render_JoinsTextBlocks_DropsTheOthersWithAPinnedLine_AndPrefixesAnError()
    {
        Assert.Equal("⟪one⟫\n⟪two⟫\n" + UntrustedText.QuoteNote, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "one" }, new TextContentBlock { Text = "two" }] }));
        Assert.Equal("(no content)", McpTool.Render(new CallToolResult { Content = [] }));
        Assert.Equal("(an image block was dropped)", McpTool.Render(new CallToolResult { Content = [new ImageContentBlock { Data = new byte[] { 1 }, MimeType = "image/png" }] }));
        Assert.Equal("(an audio block was dropped)", McpTool.Render(new CallToolResult { Content = [new AudioContentBlock { Data = new byte[] { 1 }, MimeType = "audio/wav" }] }));
        Assert.Equal("(a resource_link block was dropped)", McpText.BlockDropped("resource_link"));
        Assert.Equal("⟪text⟫\n(an image block was dropped)\n" + UntrustedText.QuoteNote, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "text" }, new ImageContentBlock { Data = new byte[] { 1 }, MimeType = "image/png" }] }));
        Assert.Equal("Error: ⟪boom⟫\n" + UntrustedText.QuoteNote, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "boom" }], IsError = true }));
        Assert.Equal("Error: ⟪already⟫\n" + UntrustedText.QuoteNote, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "Error: already" }], IsError = true }));
        Assert.Equal("Error: ⟪Errors pile up⟫\n" + UntrustedText.QuoteNote, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "Errors pile up" }], IsError = true }));
        Assert.Equal(McpText.ImageDropped, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "  " }, new ImageContentBlock { Data = new byte[] { 1 }, MimeType = "image/png" }] }));
        Assert.Equal(McpText.NoContent, McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "" }] }));
        Assert.Equal("Error: (no content)", McpTool.Render(new CallToolResult { Content = [], IsError = true }));
        Assert.Equal("Error: ", McpText.ErrorPrefix);
        Assert.True(Assistant.IsToolError(McpTool.Render(new CallToolResult { Content = [new TextContentBlock { Text = "x" }], IsError = true })));
    }

    [Fact]
    public void OneLine_CollapsesWhitespace()
    {
        Assert.Equal("Reads a file. Use for text.", McpText.OneLine("  Reads a file.\n\n   Use for\tstext.".Replace("stext", "text")));
        Assert.Equal("", McpText.OneLine(null));
        Assert.Equal("", McpText.OneLine("  \n "));
    }

    /// <summary>The whole path: the pipe server listed through the real client, wrapped, invoked through the turn loop's static, the echo back.</summary>
    [Fact]
    public async Task ThroughThePipeServer_ListedWrappedAndCalled_TheEchoComesBack_AndAFailToolIsAnError()
    {
        await using var server = McpPipeServer.Start([("echo", "Echoes  the\ntext."), (McpPipeServer.FailToolName, "Fails."), ("nodesc", ""), (McpPipeServer.ImageToolName, "A picture.")]);
        await using var client = await McpClient.CreateAsync(server.ClientTransport);
        var listed = await client.ListToolsAsync((RequestOptions?)null);
        var names = McpToolName.Unique(listed.Select(t => McpToolName.Prefixed("pipe", t.Name)).ToList());
        var tools = listed.Select((t, i) => (AIFunction)new McpTool("pipe", client, t, names[i])).ToList();

        Assert.Equal(["pipe__echo", "pipe__fail", "pipe__nodesc", "pipe__image"], tools.Select(t => t.Name));
        Assert.Equal("Echoes the text.", tools[0].Description);   // one line
        Assert.Equal("(no description)", tools[2].Description);
        var echo = (McpTool)tools[0];
        Assert.Equal("pipe", echo.ServerName);
        Assert.Equal("echo", echo.ServerToolName);
        Assert.Equal(JsonValueKind.Object, echo.JsonSchema.ValueKind);
        Assert.True(echo.JsonSchema.GetProperty("properties").TryGetProperty("text", out _));

        var (text, images) = await Assistant.InvokeToolAsync(tools, new FunctionCallContent("c1", "pipe__echo", new Dictionary<string, object?> { ["text"] = "ping" }), CancellationToken.None);
        Assert.Equal("⟪echo: ping⟫\n" + UntrustedText.QuoteNote, text);
        Assert.Empty(images);
        Assert.Equal(("echo", """{"text":"ping"}"""), server.Calls[0]);

        var (error, _) = await Assistant.InvokeToolAsync(tools, new FunctionCallContent("c2", "pipe__fail", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal("Error: ⟪boom⟫" + "\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, error);
        var (picture, none) = await Assistant.InvokeToolAsync(tools, new FunctionCallContent("c3", "pipe__image", new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Equal(McpText.ImageDropped, picture);
        Assert.Empty(none);
    }

    /// <summary>The read-only mark (2026-11-19, feature idea #30) comes from the server's <c>readOnlyHint</c>; a tool with no caller still calls its client.</summary>
    [Fact]
    public async Task ReadOnly_IsTheServersHint_AndWithoutACaller_TheToolCallsItsClient()
    {
        await using var server = McpPipeServer.Start([("look", "Looks."), ("touch", "Touches.")], readOnly: new HashSet<string> { "look" });
        await using var client = await McpClient.CreateAsync(server.ClientTransport);
        var listed = await client.ListToolsAsync((RequestOptions?)null);
        var look = new McpTool("pipe", client, listed[0], "pipe__look");
        var touch = new McpTool("pipe", client, listed[1], "pipe__touch");

        Assert.True(look.ReadOnly);
        Assert.False(touch.ReadOnly);
        var (text, _) = await Assistant.InvokeToolAsync([touch], new FunctionCallContent("c1", "pipe__touch", new Dictionary<string, object?> { ["text"] = "hi" }), CancellationToken.None);
        Assert.Equal("⟪echo: hi⟫\n" + UntrustedText.QuoteNote, text);
        Assert.Equal(("touch", """{"text":"hi"}"""), Assert.Single(server.Calls));
    }

    /// <summary>The app's own error text is unfenced; a failed reconnect's detail (the server's words) is fenced with the quote note.</summary>
    [Fact]
    public void AnOutcome_AnAppErrorIsUnfenced_AFencedOneCarriesTheNote_AnAnswerRendersAsBefore()
    {
        Assert.Equal(McpText.ErrorPrefix + "docker: the MCP servers were stopped", McpCallOutcome.Failed(McpText.StoppedError("docker")).Render());
        Assert.Equal(McpText.ErrorPrefix + "x ⟪y⟫\n" + UntrustedText.QuoteNote, McpCallOutcome.Failed("x " + UntrustedText.Fenced("y"), fenced: true).Render());
        var answer = new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] };
        Assert.Equal(McpTool.Render(answer), McpCallOutcome.Answered(answer).Render());
    }
}
