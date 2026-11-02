using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// What the Assistant says it sent (2026-11-02, the review: the <c>/usage</c> Context tab mid-reply and the mid-turn guard): the history's
/// estimate published before each request, the new message in it and none of the last turn's before the first; an earlier turn's thinking
/// counted only where the wire sends it back (<c>LLM preserve thinking</c> on the OpenAI-compatible wire, never on the Anthropic or OpenAI API).
/// </summary>
public class SentEstimateTests
{
    private static Assistant NewAssistant(FakeChatClient client) =>
        new(client, new ConversationHistory("sys"), new LlmTimeouts(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)), [], new ManualTimeProvider());

    [Fact]
    public async Task LastSentEstimate_IsPublishedBeforeEachRequest_WithTheNewMessage_AndNoneOfTheLastTurnsBeforeIt()
    {
        var client = new FakeChatClient();
        var assistant = NewAssistant(client);
        client.EnqueueText("Hi.").EnqueueText("Again.");
        await foreach (var _ in assistant.RunTurnAsync("first", []))
        {
        }

        Assert.NotNull(assistant.LastSentEstimate);
        string message = new('m', 400);
        ConversationCompactor.TokenEstimate? seen = null;
        ConversationCompactor.TokenEstimate? atStart = assistant.LastSentEstimate;
        client.BeforeUpdate = (i, _) =>
        {
            seen ??= assistant.LastSentEstimate;
            return Task.CompletedTask;
        };
        var turn = assistant.RunTurnAsync(message, []).GetAsyncEnumerator();
        try
        {
            Assert.True(await turn.MoveNextAsync());   // the first event: the request is out
            Assert.NotNull(seen);
            Assert.True(seen.Value.TextChars >= "first".Length + "Hi.".Length + message.Length);
            Assert.NotEqual(atStart, seen);
            while (await turn.MoveNextAsync())
            {
            }
        }
        finally
        {
            await turn.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("http://localhost:8080/v1", false)]
    [InlineData("https://api.anthropic.com/v1", true)]
    [InlineData("https://api.openai.com/v1", true)]
    public void InFlightOnlyWire_IsTheAnthropicAndOpenAIApis(string url, bool inFlightOnly)
    {
        Assert.Equal(inFlightOnly, Assistant.InFlightOnlyWire(new Uri(url)));
    }

    [Fact]
    public void SentEstimate_AnEarlierTurnsThinking_OnlyWhereTheWireSendsItBack()
    {
        var assistant = NewAssistant(new FakeChatClient());
        string thought = new('t', 4_000);
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "q1"),
            new(ChatRole.Assistant, [new TextReasoningContent(thought), new TextContent("a1")]),
            new(ChatRole.User, "q2"),
        ];
        long without = assistant.SentEstimate(messages);

        assistant.PreserveThinking = true;
        Assert.True(assistant.EarlierThinkingSent);
        Assert.Equal(without + thought.Length / ConversationCompactor.CharsPerToken, assistant.SentEstimate(messages));

        assistant.ThinkingInFlightOnly = true;   // the Claude API's signed blocks go back in the turn in flight alone
        Assert.False(assistant.EarlierThinkingSent);
        Assert.Equal(without, assistant.SentEstimate(messages));
    }
}
