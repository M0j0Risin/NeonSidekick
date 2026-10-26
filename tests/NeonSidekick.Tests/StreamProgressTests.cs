using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>What starts the speed's clock (2026-10-26, the user's report of over 100,000 tok/s).</summary>
public class StreamProgressTests
{
    [Theory]
    [InlineData("text", true)]
    [InlineData("empty text", false)]   // a server's content "" role chunk
    [InlineData("role only", false)]
    [InlineData("reasoning", true)]
    [InlineData("empty reasoning", false)]
    [InlineData("signature", true)]   // the end of real thinking
    [InlineData("usage", false)]
    [InlineData("call", true)]
    [InlineData("marker", true)]
    [InlineData("other raw", false)]
    public void Shows_IsTrue_OnlyForSomethingTheModelProduced(string kind, bool shows)
    {
        var update = kind switch
        {
            "text" => FakeChatClient.Text("Hi"),
            "empty text" => FakeChatClient.Text(""),
            "role only" => new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>()),
            "reasoning" => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("hmm")]),
            "empty reasoning" => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("")]),
            "signature" => new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("") { ProtectedData = "sig" }]),
            "usage" => FakeChatClient.Usage(10, 5),
            "call" => FakeChatClient.Call("c1", "read_file"),
            "marker" => FakeChatClient.Progress(),
            _ => new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>()) { RawRepresentation = new object() },
        };

        Assert.Equal(shows, StreamProgress.Shows(update));
    }
}
