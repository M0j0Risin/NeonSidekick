using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class ConversationHistoryTests
{
    [Fact]
    public void BuildRequest_PutsTheSystemPromptFirst_ThenTheTranscript()
    {
        var history = new ConversationHistory("be brief");
        history.AddUser("hi");
        history.AddAssistant("hello");

        var request = history.BuildRequest();

        Assert.Equal(3, request.Count);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal("be brief", request[0].Text);
        Assert.Equal(ChatRole.User, request[1].Role);
        Assert.Equal(ChatRole.Assistant, request[2].Role);
        Assert.Equal(2, history.Messages.Count); // the system prompt is not a transcript message
    }

    [Fact]
    public void BlankSystemPrompt_IsOmitted()
    {
        var history = new ConversationHistory("   ");
        history.AddUser("hi");
        Assert.Single(history.BuildRequest());
    }

    [Fact]
    public void BuildRequest_ReturnsACopy()
    {
        var history = new ConversationHistory("s");
        history.AddUser("a");
        var request = history.BuildRequest();
        history.AddUser("b");
        Assert.Equal(2, request.Count);
        Assert.Equal(3, history.BuildRequest().Count);
    }

    [Fact]
    public void ToolResults_BecomeOneToolMessage_AndEmptyOnesAreIgnored()
    {
        var history = new ConversationHistory("s");
        history.AddUser("u");
        history.AddMessage(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "echo") }));
        history.AddToolResults(new[] { new FunctionResultContent("c1", "r1"), new FunctionResultContent("c2", "r2") });
        history.AddToolResults(Array.Empty<FunctionResultContent>());
        history.AddMessage(new ChatMessage(ChatRole.Assistant, new List<AIContent>()));

        Assert.Equal(3, history.Messages.Count);
        var tool = history.Messages[2];
        Assert.Equal(ChatRole.Tool, tool.Role);
        Assert.Equal(new[] { "c1", "c2" }, tool.Contents.OfType<FunctionResultContent>().Select(r => r.CallId));
    }

    [Fact]
    public void NoCap_KeepsEveryTurn()
    {
        var history = new ConversationHistory("s") { MaxTurns = null };
        for (int i = 1; i <= ConversationHistory.DefaultMaxTurns + 6; i++)
        {
            history.AddUser($"u{i}");
            history.AddAssistant($"a{i}");
        }

        Assert.Equal(ConversationHistory.DefaultMaxTurns + 6, history.TurnCount);
        Assert.Equal("u1", history.Messages[0].Text);
    }

    [Fact]
    public void LoweringTheCap_TrimsOnTheNextUserMessage()
    {
        var history = new ConversationHistory("s") { MaxTurns = null };
        for (int i = 1; i <= 10; i++)
        {
            history.AddUser($"u{i}");
            history.AddAssistant($"a{i}");
        }

        history.MaxTurns = 4;
        Assert.Equal(10, history.TurnCount);

        history.AddUser("u11");

        Assert.Equal(4, history.TurnCount);
        Assert.Equal("u8", history.Messages[0].Text);
    }

    [Fact]
    public void Trimming_DropsWholeTurns_NeverSeparatingACallFromItsResult()
    {
        var history = new ConversationHistory("s");
        for (int i = 1; i <= ConversationHistory.DefaultMaxTurns + 3; i++)
        {
            history.AddUser($"u{i}");
            history.AddMessage(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent($"call{i}", "echo") }));
            history.AddToolResults(new[] { new FunctionResultContent($"call{i}", "ok") });
            history.AddAssistant($"a{i}");
        }

        Assert.Equal(ConversationHistory.DefaultMaxTurns, history.TurnCount);
        Assert.Equal(ChatRole.User, history.Messages[0].Role);
        Assert.Equal("u4", history.Messages[0].Text);

        var seenCalls = new HashSet<string>();
        foreach (var message in history.Messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>()) seenCalls.Add(call.CallId);
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                Assert.Contains(result.CallId, seenCalls);
            }
        }
    }

    [Fact]
    public void TryReplaceToolResult_ReplacesInPlace_FalseWhenAbsentOrUnchanged()
    {
        var history = new ConversationHistory("s");
        history.AddUser("u");
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("neoncwdir", "get_working_directory", new Dictionary<string, object?>())]));
        history.AddToolResults([new FunctionResultContent("neoncwdir", "D:\\old"), new FunctionResultContent("other", "kept")]);
        history.AddAssistant("reply");

        Assert.False(history.TryReplaceToolResult("missing", "x"));
        Assert.False(history.TryReplaceToolResult("neoncwdir", "D:\\old"));
        Assert.True(history.TryReplaceToolResult("neoncwdir", "D:\\new"));

        // Same four messages in the same order; only the one result's text changed.
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant }, history.Messages.Select(m => m.Role));
        var results = history.Messages[2].Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(("neoncwdir", "D:\\new"), (results[0].CallId, results[0].Result));
        Assert.Equal(("other", "kept"), (results[1].CallId, results[1].Result));
        Assert.Equal("reply", history.Messages[3].Text);
    }

    [Fact]
    public void Clear_ForgetsMessages_KeepsThePrompt()
    {
        var history = new ConversationHistory("s");
        history.AddUser("u");
        history.Clear();
        Assert.Empty(history.Messages);
        Assert.Equal("s", history.SystemPrompt);
        Assert.Single(history.BuildRequest());
    }

    [Fact]
    public void AddUser_WithImages_IsTheTextThenOneImagePartEach_InOrder()
    {
        var history = new ConversationHistory("be brief");
        var png = new ImageAttachment(@"C:\a.png", [1, 2, 3], ImageFile.Png, 4, 4);
        var jpeg = new ImageAttachment(@"C:\b.jpg", [4, 5], ImageFile.Jpeg, 8, 8);

        history.AddUser("look at [Image #1] and [Image #2]", [png, jpeg]);

        var message = Assert.Single(history.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal(3, message.Contents.Count);
        Assert.Equal("look at [Image #1] and [Image #2]", Assert.IsType<TextContent>(message.Contents[0]).Text);
        var first = Assert.IsType<DataContent>(message.Contents[1]);
        Assert.Equal(ImageFile.Png, first.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Data.ToArray());
        Assert.Equal(ImageFile.Jpeg, Assert.IsType<DataContent>(message.Contents[2]).MediaType);
        Assert.Equal("look at [Image #1] and [Image #2]", message.Text);
    }

    [Fact]
    public void AddUser_WithNoImages_IsThePlainTextMessage()
    {
        var history = new ConversationHistory("be brief");

        history.AddUser("hi", []);

        var message = Assert.Single(history.Messages);
        Assert.Equal("hi", Assert.IsType<TextContent>(Assert.Single(message.Contents)).Text);
        Assert.Throws<ArgumentNullException>(() => history.AddUser("hi", null!));
        Assert.Throws<ArgumentNullException>(() => history.AddUser(null!, []));
    }

    [Fact]
    public void Replace_SwapsTheTranscript_SkipsEmptyMessages_AndTrims()
    {
        var history = new ConversationHistory("s");
        history.AddUser("old");
        history.AddAssistant("reply");

        history.Replace([new ChatMessage(ChatRole.User, "summary"), new ChatMessage(ChatRole.Assistant, new List<AIContent>()), new ChatMessage(ChatRole.User, "recent")]);

        Assert.Equal(new[] { "summary", "recent" }, history.Messages.Select(m => m.Text));
        Assert.Equal(2, history.TurnCount);
        Assert.Equal("s", history.SystemPrompt);

        var many = new List<ChatMessage>();
        for (int i = 0; i < ConversationHistory.DefaultMaxTurns + 2; i++)
        {
            many.Add(new ChatMessage(ChatRole.User, "u" + i));
            many.Add(new ChatMessage(ChatRole.Assistant, "a" + i));
        }

        history.Replace(many);

        Assert.Equal(ConversationHistory.DefaultMaxTurns, history.TurnCount);
        Assert.Equal("u2", history.Messages[0].Text);
    }

    [Fact]
    public void AddToolImages_AppendsOneTaggedUserMessage_ThatIsNotATurn()
    {
        var history = new ConversationHistory("s");
        history.AddUser("look");
        history.AddMessage(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "view_image") }));
        history.AddToolResults([new FunctionResultContent("c1", "pic.png (4x4): next")]);
        history.AddToolImages([new ImageAttachment("pic.png", [1, 2], ImageFile.Png, 4, 4), new ImageAttachment("b.jpg", [3], ImageFile.Jpeg, 1, 1)]);
        history.AddToolImages([]);

        Assert.Equal(4, history.Messages.Count);
        var carrier = history.Messages[3];
        Assert.Equal(ChatRole.User, carrier.Role);
        Assert.True(ConversationHistory.IsImageCarrier(carrier));
        Assert.False(ConversationHistory.IsTurnStart(carrier));
        Assert.True(ConversationHistory.IsTurnStart(history.Messages[0]));
        Assert.False(ConversationHistory.IsImageCarrier(history.Messages[0]));
        Assert.Equal(1, history.TurnCount);
        Assert.Equal(ConversationHistory.ImageCarrierText(["pic.png", "b.jpg"]), carrier.Text);
        Assert.Equal([ImageFile.Png, ImageFile.Jpeg], carrier.Contents.OfType<DataContent>().Select(d => d.MediaType));
        Assert.Equal(true, carrier.AdditionalProperties![ConversationHistory.CarrierKey]);
    }

    [Fact]
    public void ImageCarrierText_IsPinned()
    {
        Assert.Equal("(attached by view_image, not typed by the user: ladybug.png)", ConversationHistory.ImageCarrierText(["ladybug.png"]));
        Assert.Equal("(attached by view_image, not typed by the user — the pictures: a.png, b.jpg)", ConversationHistory.ImageCarrierText(["a.png", "b.jpg"]));
        Assert.Equal("neon.imageCarrier", ConversationHistory.CarrierKey);
    }

    [Fact]
    public void Trimming_KeepsACarrierWithItsTurn_AndDropsItWithIt()
    {
        var history = new ConversationHistory("s");
        for (int i = 1; i <= ConversationHistory.DefaultMaxTurns + 1; i++)
        {
            history.AddUser($"u{i}");
            if (i is 1 or 2)
            {
                history.AddMessage(new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent($"call{i}", "view_image") }));
                history.AddToolResults([new FunctionResultContent($"call{i}", "next")]);
                history.AddToolImages([new ImageAttachment($"p{i}.png", [1], ImageFile.Png, 1, 1)]);
            }

            history.AddAssistant($"a{i}");
        }

        // Turn 1 and its carrier went together; turn 2's carrier still sits between its result and its reply.
        Assert.Equal(ConversationHistory.DefaultMaxTurns, history.TurnCount);
        Assert.Equal("u2", history.Messages[0].Text);
        Assert.True(ConversationHistory.IsImageCarrier(history.Messages[3]));
        Assert.Equal("a2", history.Messages[4].Text);
        Assert.Single(history.Messages, ConversationHistory.IsImageCarrier);
    }

    [Fact]
    public void LastTurns_CountsBackByTurnStarts_CarriersNotBoundaries()
    {
        var history = new ConversationHistory("s");
        Assert.Empty(history.LastTurns(1));
        Assert.Empty(history.LastTurn());

        history.AddUser("one");
        history.AddAssistant("r1");
        history.AddUser("two");
        history.AddToolImages([new ImageAttachment("a.png", [1], ImageFile.Png, 1, 1)]);   // a carrier: no boundary
        history.AddAssistant("r2");
        history.AddUser("three");
        history.AddAssistant("r3");

        Assert.Equal(["three", "r3"], history.LastTurn().Select(m => m.Text));
        Assert.Equal(["three", "r3"], history.LastTurns(1).Select(m => m.Text));
        Assert.Equal(5, history.LastTurns(2).Count);
        Assert.Equal("two", history.LastTurns(2)[0].Text);
        Assert.Equal(7, history.LastTurns(3).Count);
        Assert.Equal(7, history.LastTurns(5).Count);   // fewer held than asked: everything
        Assert.Throws<ArgumentOutOfRangeException>(() => history.LastTurns(0));

        // A copy: the history is untouched by an edit to it.
        var copy = history.LastTurns(2);
        copy.Clear();
        Assert.Equal(7, history.Messages.Count);
    }

    [Fact]
    public void RemoveLastTurn_DropsTheUserMessage_AndEverythingAfterIt()
    {
        var history = new ConversationHistory("s");
        Assert.False(history.RemoveLastTurn());   // nothing held

        history.AddUser("one");
        history.AddAssistant("r1");
        // A withdrawn first turn: the user message, then the opening call and its result.
        history.AddUser("two");
        var call = new FunctionCallContent("neonclk01", "get_current_time", new Dictionary<string, object?>());
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [call]));
        history.AddToolResults([new FunctionResultContent(call.CallId, "14:05")]);
        history.AddToolImages([new ImageAttachment("a.png", [1], ImageFile.Png, 1, 1)]);   // a carrier goes with its turn
        Assert.Equal(2, history.TurnCount);

        Assert.True(history.RemoveLastTurn());

        Assert.Equal(["one", "r1"], history.Messages.Select(m => m.Text));
        Assert.Equal(1, history.TurnCount);

        // Again: the turn before it; then nothing.
        Assert.True(history.RemoveLastTurn());
        Assert.Empty(history.Messages);
        Assert.Equal(0, history.TurnCount);
        Assert.False(history.RemoveLastTurn());
    }
}
