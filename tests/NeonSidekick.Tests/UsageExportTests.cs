using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
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

        Assert.StartsWith(UsageExport.Heading + "\nExported 2026-11-23 19:42:07 · model qwen3 · profile neon · ~17.3k tokens of 151.4k\n" + UsageExport.AsSentLine + "\n", md);
        Assert.Contains("## System message (~9.8k tokens)\n```text\nYou are Neon.\nBe brief.\n```\n", md);
        Assert.Contains("## Tools (1, ~120 tokens)\n" + UsageExport.SchemaLine + "\n\n### echo\n```text\nEchoes the text back.\n```\n```json\n{\n  \"type\": \"object\",", md);
        Assert.Contains("## History (4 messages, ~7.4k tokens)\n", md);
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
    }

    [Fact]
    public void Markdown_NoModelYet_SaysSo()
    {
        var facts = Facts([]) with { Model = null, Window = null };
        Assert.Contains("· model not known yet · profile neon · ~17.3k tokens\n", UsageExport.Markdown(facts));
    }

    [Fact]
    public void ExportFileName_IsStampedToTheMillisecond_ThenNumbered()
    {
        Assert.Equal("neon-sysprompt-20261123-194207-318.md", UsageText.ExportFileName(At, 1));
        Assert.Equal("neon-sysprompt-20261123-194207-318-2.md", UsageText.ExportFileName(At, 2));
    }

    [Theory]
    [InlineData("sysprompt", true, null, null)]
    [InlineData("SYSPROMPT to=x", true, "x", null)]
    [InlineData("sysprompt to=x/", true, "x/", null)]
    [InlineData("sysprompt \"to=my notes/c.md\"", true, "my notes/c.md", null)]
    [InlineData("sysprompt foo", true, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt to=", true, null, UsageText.SyspromptUsage)]
    [InlineData("sysprompt to=a to=b", true, null, UsageText.SyspromptUsage)]
    [InlineData("prompt", false, null, null)]
    [InlineData("syspromptx", false, null, null)]
    [InlineData("", false, null, null)]
    public void ParseSysprompt_IsPinned(string args, bool isIt, string? to, string? error)
    {
        Assert.Equal((isIt, to, error), UsageText.ParseSysprompt(args));
        Assert.Equal(isIt, UsageText.IsSysprompt(args));
    }

    [Fact]
    public void ToAnotherDocumentType_IsRefused()
    {
        Assert.NotNull(OutputNames.Refusal("out.pdf", UsageText.ExportExtension, _ => false));
        Assert.Null(OutputNames.Refusal("ctx", UsageText.ExportExtension, _ => false));
    }
}
