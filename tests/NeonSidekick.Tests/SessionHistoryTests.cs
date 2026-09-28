using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;

namespace NeonSidekick.Tests;

public class SessionHistoryTests
{
    private static ChatMessage CallMessage(string callId, string name, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new TextContent("Let me look."), new FunctionCallContent(callId, name, arguments)]);

    [Fact]
    public void ATextTurn_RoundTrips()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello"), new(ChatRole.Assistant, "Hi there.") };

        var back = SessionHistory.FromJson(SessionHistory.ToJson(messages));

        Assert.Equal(2, back.Count);
        Assert.Equal(ChatRole.User, back[0].Role);
        Assert.Equal("hello", back[0].Text);
        Assert.Equal(ChatRole.Assistant, back[1].Role);
        Assert.Equal("Hi there.", back[1].Text);
        Assert.False(ConversationHistory.IsImageCarrier(back[0]));
    }

    [Fact]
    public void AUserTurnWithAPicture_KeepsTheBytesAndTheMediaType()
    {
        var history = new ConversationHistory("");
        history.AddUser("look [Image #1]", [new ImageAttachment("cat.png", [1, 2, 3, 4], "image/png", 2, 2)]);

        var back = SessionHistory.FromJson(SessionHistory.ToJson(history.Messages));

        var message = Assert.Single(back);
        Assert.Equal(2, message.Contents.Count);
        Assert.Equal("look [Image #1]", Assert.IsType<TextContent>(message.Contents[0]).Text);
        var image = Assert.IsType<DataContent>(message.Contents[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Data.ToArray());
    }

    [Fact]
    public void AToolCallAndItsResult_RoundTrip_TheArgumentsAsJsonElements()
    {
        var call = CallMessage("call_1", "read_file", new Dictionary<string, object?> { ["path"] = "notes.md", ["lines"] = 3, ["all"] = true, ["none"] = null });
        var history = new ConversationHistory("");
        history.AddUser("read it");
        history.AddMessage(call);
        history.AddToolResults([new FunctionResultContent("call_1", "1: hello")]);
        history.AddAssistant("It says hello.");

        string json = SessionHistory.ToJson(history.Messages);
        var back = SessionHistory.FromJson(json);

        Assert.Equal(4, back.Count);
        var restoredCall = Assert.Single(back[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", restoredCall.CallId);
        Assert.Equal("read_file", restoredCall.Name);
        Assert.NotNull(restoredCall.Arguments);
        Assert.Equal("notes.md", Assert.IsType<JsonElement>(restoredCall.Arguments["path"]).GetString());
        Assert.Equal(3, Assert.IsType<JsonElement>(restoredCall.Arguments["lines"]).GetInt32());
        Assert.True(Assert.IsType<JsonElement>(restoredCall.Arguments["all"]).GetBoolean());
        Assert.Equal(JsonValueKind.Null, Assert.IsType<JsonElement>(restoredCall.Arguments["none"]).ValueKind);
        Assert.Equal("Let me look.", back[1].Text);
        var result = Assert.Single(back[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal(ChatRole.Tool, back[2].Role);
        Assert.Equal("call_1", result.CallId);
        Assert.Equal("1: hello", result.Result);
        Assert.False(ConversationHistory.IsSkillResult(result));
        // The arguments went out through the same writer the log uses, so a second trip is byte-identical.
        Assert.Equal(json, SessionHistory.ToJson(back));
    }

    [Fact]
    public void ASkillResult_KeepsItsTag()
    {
        var history = new ConversationHistory("");
        history.AddUser("#haiku write one");
        var call = new FunctionCallContent("call_1", "load_skill", new Dictionary<string, object?> { ["name"] = "haiku" });
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [call]));
        history.AddToolResults([Assistant.ResultContent(call, "<skill_content name=\"haiku\">write haiku</skill_content>")]);
        var second = new FunctionCallContent("call_2", "load_skill", new Dictionary<string, object?> { ["name"] = "sonnet" });
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [second]));
        history.AddToolResults([Assistant.ResultContent(second, "Error: no such skill")]);

        var back = SessionHistory.FromJson(SessionHistory.ToJson(history.Messages));

        Assert.True(ConversationHistory.IsSkillResult(Assert.Single(back[2].Contents.OfType<FunctionResultContent>())));
        Assert.False(ConversationHistory.IsSkillResult(Assert.Single(back[4].Contents.OfType<FunctionResultContent>())));
        Assert.Equal("call_1", Assert.Single(back[1].Contents.OfType<FunctionCallContent>()).CallId);
    }

    [Fact]
    public void AnImageCarrier_KeepsItsMark_SoItIsNoTurn()
    {
        var history = new ConversationHistory("");
        history.AddUser("show me");
        history.AddToolImages([new ImageAttachment("ladybug.png", [9, 9], "image/png", 1, 2)]);
        history.AddAssistant("A ladybug.");

        var back = SessionHistory.FromJson(SessionHistory.ToJson(history.Messages));

        Assert.Equal(3, back.Count);
        Assert.True(ConversationHistory.IsImageCarrier(back[1]));
        Assert.Equal(ConversationHistory.ImageCarrierText(["ladybug.png"]), Assert.IsType<TextContent>(back[1].Contents[0]).Text);
        var restored = new ConversationHistory("");
        restored.Restore(back);
        Assert.Equal(1, restored.TurnCount);
    }

    [Fact]
    public void ACompactionSummary_IsAnOrdinaryUserMessage()
    {
        var summary = new ChatMessage(ChatRole.User, ConversationCompactor.SummaryPreamble + "we talked about cats");
        var back = SessionHistory.FromJson(SessionHistory.ToJson([summary, new ChatMessage(ChatRole.User, "and dogs?")]));

        Assert.Equal(ChatRole.User, back[0].Role);
        Assert.StartsWith(ConversationCompactor.SummaryPreamble, back[0].Text);
        Assert.True(ConversationHistory.IsTurnStart(back[0]));
    }

    [Fact]
    public void PartsThatAreNotConversation_AreDropped_AndAnEmptyMessageWithThem()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 3 })]),
            new(ChatRole.User, "kept"),
        };

        var back = SessionHistory.FromJson(SessionHistory.ToJson(messages));

        Assert.Equal("kept", Assert.Single(back).Text);
    }

    /// <summary>A reply's thinking (2026-09-28): saved only when asked (<c>Session save thinking</c>), read back whenever it is there.</summary>
    [Fact]
    public void Thinking_IsSavedOnlyWhenAsked_AndReadBackAsThinking()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "q"),
            new(ChatRole.Assistant, [new TextReasoningContent("musing"), new TextContent("answer")]),
        };

        var without = SessionHistory.FromJson(SessionHistory.ToJson(messages));
        Assert.Empty(without[1].Contents.OfType<TextReasoningContent>());
        Assert.Equal("answer", without[1].Text);

        var with = SessionHistory.FromJson(SessionHistory.ToJson(messages, withThinking: true));
        Assert.Equal("musing", Assert.Single(with[1].Contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("answer", with[1].Text);
        Assert.IsType<TextReasoningContent>(with[1].Contents[0]);   // in its place, ahead of the text
    }

    [Fact]
    public void ADocumentThatIsNotAHistory_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => SessionHistory.FromJson("not json"));
        Assert.ThrowsAny<JsonException>(() => SessionHistory.FromJson("null"));
        Assert.Empty(SessionHistory.FromJson("{}"));
    }
}
