using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/usage sysprompt</c>'s Markdown and words (2026-11-23, the user's ask): the system message, each tool and each message word for word, a
/// fence no text inside can close, pictures as placeholders, thinking only when the request sends it; the file's name and the parse.
/// </summary>
public sealed class UsageExportTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 19, 42, 7, 318, TimeSpan.Zero);

    private static UsageExportFacts Facts(IReadOnlyList<ChatMessage> messages, int thinkingFrom = 0, string prompt = "You are Neon.\nBe brief.") =>
        new(At, "qwen3", "neon", 151_400, prompt, 9_800, [new EchoTool()], 120, messages, thinkingFrom, 7_400);

    [Fact]
    public void Markdown_HoldsThePrompt_TheTools_AndTheMessagesInOrder()
    {
        var call = new FunctionCallContent("call_ab12", "echo", new Dictionary<string, object?> { ["text"] = "hi" });
        string md = UsageExport.Markdown(Facts(
        [
            new ChatMessage(ChatRole.User, "say hi"),
            new ChatMessage(ChatRole.Assistant, [new TextContent("Calling."), call]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call_ab12", "echo: hi")]),
            new ChatMessage(ChatRole.Assistant, "Done."),
        ]));

        Assert.StartsWith(UsageExport.Heading + "\n\n- **Exported:** 2026-11-23 19:42:07\n- **Model:** `qwen3`\n- **Profile:** `neon`\n"
            + "- **Context:** ~17.3k of 151.4k tokens (11%): system message ~9.8k, tools ~120, conversation ~7.4k\n\n*" + UsageExport.AsSentLine + "*\n", md);
        Assert.Contains("*\n\n" + UsageExport.ContentsLine + "\n\n## System message\n", md);
        Assert.Equal("**Contents:** [System message](#system-message) · [Tools](#tools) · [Conversation](#conversation)", UsageExport.ContentsLine);
        Assert.Contains("## System message\n~9.8k tokens.\n```text\nYou are Neon.\nBe brief.\n```\n", md);
        Assert.Contains("## Tools\n1 tool, ~120 tokens.\n" + UsageExport.SchemaLine + "\n\n### echo\n```text\nEchoes the text back.\n```\n```json\n{\n  \"type\": \"object\",", md);
        Assert.Contains("## Conversation\n4 messages, ~7.4k tokens.\n", md);
        Assert.DoesNotContain(UsageExport.NoMessagesLine, md);
        int user = md.IndexOf("### 1 · user\n```text\nsay hi\n```", StringComparison.Ordinal);
        int asked = md.IndexOf("### 2 · assistant\n```text\nCalling.\n```\nCalls `echo` (call_ab12):\n```json\n{\n  \"text\": \"hi\"\n}\n```", StringComparison.Ordinal);
        int result = md.IndexOf("### 3 · tool\nResult of `echo` (call_ab12):\n```text\necho: hi\n```", StringComparison.Ordinal);
        int done = md.IndexOf("### 4 · assistant\n```text\nDone.\n```", StringComparison.Ordinal);
        Assert.True(user > 0 && asked > user && result > asked && done > result, md);
        Assert.DoesNotContain("\r", md);
    }

    [Fact]
    public void Markdown_AFenceInsideATextGetsALongerFence()
    {
        string md = UsageExport.Markdown(Facts([new ChatMessage(ChatRole.User, "look:\n```cs\nx\n```")]));
        Assert.Contains("````text\nlook:\n```cs\nx\n```\n````\n", md);
    }

    [Fact]
    public void Markdown_APicture_IsAPlaceholder_NeverItsBytes()
    {
        var picture = new DataContent(new byte[412 * 1024], "image/png");
        string md = UsageExport.Markdown(Facts([new ChatMessage(ChatRole.User, [new TextContent("what is this?"), picture])]));
        Assert.Contains("[picture: image/png, 412 KB]\n", md);
        Assert.DoesNotContain("AAAA", md);
        Assert.Equal("[file: application/pdf, 1 KB]", UsageExport.Placeholder("application/pdf", 10));
    }

    [Fact]
    public void Markdown_Thinking_OnlyWhereTheRequestSendsIt()
    {
        ChatMessage[] messages =
        [
            new ChatMessage(ChatRole.User, "q1"),
            new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("old thought"), new TextContent("a1")]),
        ];
        string sent = UsageExport.Markdown(Facts(messages, thinkingFrom: 0));
        Assert.Contains("```thinking\nold thought\n```\n", sent);
        Assert.DoesNotContain(UsageExport.ThinkingLeftOutLine, sent);

        string left = UsageExport.Markdown(Facts(messages, thinkingFrom: messages.Length));
        Assert.DoesNotContain("old thought", left);
        Assert.Contains(UsageExport.ThinkingLeftOutLine, left);

        // Left out because the server takes back only the turn in flight's (the last-7 review's #22): the line says so, not the setting.
        string inFlight = UsageExport.Markdown(Facts(messages, thinkingFrom: messages.Length) with { ThinkingInFlightOnly = true });
        Assert.Contains(UsageExport.ThinkingInFlightOnlyLine, inFlight);
        Assert.DoesNotContain(UsageExport.ThinkingLeftOutLine, inFlight);
        Assert.Equal("Thinking from earlier turns isn't sent (this server takes back only the turn in flight's), so it is left out.", UsageExport.ThinkingInFlightOnlyLine);
    }

    /// <summary>
    /// The last-7 review's #62: before the first message, the line names the opening calls the next request carries — the clock, the working
    /// folder, the memory list, each only when its tool is offered, in that order; with none offered, only the message.
    /// </summary>
    [Fact]
    public void Markdown_NoMessagesYet_NamesTheOpeningCalls_OfTheToolsOffered()
    {
        Assert.EndsWith(UsageExport.NoMessagesLine + "\n", UsageExport.Markdown(Facts([]) with { Tools = [] }));

        AIFunction[] tools = [new NamedTool(RecallMemoryTool.ToolName), new NamedTool("echo"), new NamedTool(GetCurrentTimeTool.ToolName), new NamedTool(GetWorkingDirectoryTool.ToolName)];
        string md = UsageExport.Markdown(Facts([]) with { Tools = tools });
        Assert.EndsWith("No messages yet: the next request carries the app's opening calls (`get_current_time`, `get_working_directory`, `recall_memory`) and then the message you send.\n", md);
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`recall_memory`) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([RecallMemoryTool.ToolName]));
    }

    /// <summary>
    /// The process polls the next request carries for notified exits (2026-11-24, review #58): named last, once, with a count past one; polls alone
    /// still make it a calls line.
    /// </summary>
    [Fact]
    public void NoMessagesWithCallsLine_NamesThePendingPolls_Last()
    {
        Assert.Equal(UsageExport.NoMessagesLine, UsageExport.NoMessagesWithCallsLine([], 0));
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`process`) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([], 1));
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`process` (2 polls)) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([], 2));
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`recall_memory`) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([RecallMemoryTool.ToolName], 0));
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`recall_memory`, `process`) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([RecallMemoryTool.ToolName], 1));
        Assert.Equal("No messages yet: the next request carries the app's opening calls (`recall_memory`, `process` (2 polls)) and then the message you send.",
            UsageExport.NoMessagesWithCallsLine([RecallMemoryTool.ToolName], 2));
        string md = UsageExport.Markdown(Facts([]) with { Tools = [new NamedTool(RecallMemoryTool.ToolName)], PendingPolls = 2 });
        Assert.EndsWith(UsageExport.NoMessagesWithCallsLine([RecallMemoryTool.ToolName], 2) + "\n", md);
    }

    private sealed class NamedTool(string name) : AIFunction
    {
        public override string Name => name;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("ok");
    }

    [Fact]
    public void Markdown_NoModelYet_SaysSo()
    {
        var facts = Facts([]) with { Model = null, Window = null };
        string md = UsageExport.Markdown(facts);
        Assert.Contains("- **Model:** " + UsageExport.NoModelYet + "\n", md);
        Assert.Contains("- **Context:** ~17.3k tokens: system message ~9.8k, tools ~120, conversation ~7.4k\n", md);
        Assert.EndsWith("## Conversation\n0 messages, ~7.4k tokens.\n" + UsageExport.NoMessagesLine + "\n", md);
    }

    /// <summary>The header's facts one list item each (2026-11-23, the user's ask), the working folder among them, each name in code no backtick closes.</summary>
    [Fact]
    public void Markdown_TheHeader_NamesTheWorkingFolder_InCodeNoBacktickCloses()
    {
        string md = UsageExport.Markdown(Facts([]) with { Folder = @"C:\My Project2", Model = "odd`model" });
        Assert.Contains("- **Working folder:** `C:\\My Project2`\n", md);
        Assert.Contains("- **Model:** `` odd`model ``\n", md);
        Assert.DoesNotContain("Working folder", UsageExport.Markdown(Facts([])));
    }

    [Fact]
    public void ExportFileName_IsStampedToTheMillisecond_ThenNumbered()
    {
        Assert.Equal("neon-sysprompt-20261123-194207-318.md", UsageText.ExportFileName(At, 1));
        Assert.Equal("neon-sysprompt-20261123-194207-318-2.md", UsageText.ExportFileName(At, 2));
    }

    [Theory]
    [InlineData("sysprompt", true, false, null, null)]
    [InlineData("SYSPROMPT to=x", true, false, "x", null)]
    [InlineData("sysprompt to=x/", true, false, "x/", null)]
    [InlineData("sysprompt \"to=my notes/c.md\"", true, false, "my notes/c.md", null)]
    [InlineData("sysprompt conversation", true, true, null, null)]
    [InlineData("sysprompt CONVERSATION to=x", true, true, "x", null)]
    [InlineData("sysprompt to=x conversation", true, true, "x", null)]
    [InlineData("sysprompt foo", true, false, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt to=", true, false, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt to=a to=b", true, false, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt conversation conversation", true, false, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt --conversation", true, false, null, UsageText.SyspromptUsage)]
    [InlineData("prompt", false, false, null, null)]
    [InlineData("syspromptx", false, false, null, null)]
    [InlineData("", false, false, null, null)]
    public void ParseSysprompt_IsPinned(string args, bool isIt, bool conversation, string? to, string? error)
    {
        Assert.Equal((isIt, conversation, to, error), UsageText.ParseSysprompt(args));
        Assert.Equal(isIt, UsageText.IsSysprompt(args));
    }

    /// <summary>A bare <c>/usage sysprompt</c> (2026-11-23, the user's ask): the system message and the tools, no conversation section or figure.</summary>
    [Fact]
    public void Markdown_WithoutTheConversation_HoldsThePromptAndTools_NoConversationFigure()
    {
        string md = UsageExport.Markdown(Facts([new ChatMessage(ChatRole.User, "secret")]) with { Messages = null });
        Assert.StartsWith(UsageExport.PromptHeading + "\n\n", md);
        Assert.Contains("- **Size:** ~9.9k of 151.4k tokens (7%): system message ~9.8k, tools ~120\n", md);
        Assert.Contains("\n\n" + UsageExport.PromptContentsLine + "\n\n## System message\n", md);
        Assert.EndsWith("```\n", md);   // the last tool's schema
        Assert.DoesNotContain("onversation", md);
        Assert.DoesNotContain("secret", md);
        Assert.Equal("(wrote the system prompt to a.md: the system message and 74 tools.)", UsageText.Exported("a.md", 74, null));
    }

    [Fact]
    public void ToAnotherDocumentType_IsRefused()
    {
        Assert.NotNull(OutputNames.Refusal("out.pdf", UsageText.ExportExtension, _ => false));
        Assert.Null(OutputNames.Refusal("ctx", UsageText.ExportExtension, _ => false));
    }
}
