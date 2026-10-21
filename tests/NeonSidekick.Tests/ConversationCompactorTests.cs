using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class ConversationCompactorTests
{
    private static readonly LlmTimeouts Timeouts = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    private static ChatMessage User(string text) => new(ChatRole.User, text);
    private static ChatMessage Assistant(string text) => new(ChatRole.Assistant, text);
    private static ChatMessage Call(string callId, string name) => new(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]);
    private static ChatMessage Result(string callId, string text) => new(ChatRole.Tool, [new FunctionResultContent(callId, text)]);
    private static string Long(int length) => new('x', length);

    /// <summary>Three turns as the app writes them: the opening pairs after the first user message, a file read in the second turn.</summary>
    private static List<ChatMessage> ThreeTurns() =>
    [
        User("one"),
        Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_current_time"), Result(NeonSidekick.Llm.Assistant.OpeningClockCallId, "Friday"),
        Call(NeonSidekick.Llm.Assistant.OpeningCwdCallId, "get_working_directory"), Result(NeonSidekick.Llm.Assistant.OpeningCwdCallId, @"D:\files"),
        Assistant("reply one"),
        User("two"),
        Call("c1", "read_file"), Result("c1", Long(500)),
        Assistant("reply two"),
        User("three"),
        Assistant("reply three"),
    ];

    private static IEnumerable<string> Roles(IEnumerable<ChatMessage> messages) => messages.Select(m => m.Role.Value);

    [Fact]
    public void Split_KeepsTheLastTurnsFromAUserBoundary_AndListsTheOpeningPairs()
    {
        var messages = ThreeTurns();

        var plan = ConversationCompactor.Split(messages, keepRecent: 1);

        Assert.Equal(new[] { "user", "assistant" }, Roles(plan.Recent));
        Assert.Equal("three", plan.Recent[0].Text);
        Assert.Equal(10, plan.Older.Count);
        Assert.Equal("one", plan.Older[0].Text);
        Assert.Equal(4, plan.Opening.Count);
        Assert.Same(messages[1], plan.Opening[0]);
        Assert.Same(messages[4], plan.Opening[3]);
        Assert.True(plan.HasOlderTurns);
    }

    [Fact]
    public void Split_TwoTurns_LeavesTheFirstTurnOnly()
    {
        var plan = ConversationCompactor.Split(ThreeTurns(), keepRecent: 2);

        Assert.Equal("two", plan.Recent[0].Text);
        Assert.Equal(6, plan.Older.Count);
        Assert.True(plan.HasOlderTurns);   // the first user message and its reply, beyond the pairs
    }

    [Fact]
    public void Split_ZeroKeepsNothing_AndMoreThanTheTurnsKeepsEverything()
    {
        var all = ConversationCompactor.Split(ThreeTurns(), keepRecent: 0);
        Assert.Empty(all.Recent);
        Assert.Equal(12, all.Older.Count);

        var none = ConversationCompactor.Split(ThreeTurns(), keepRecent: 3);
        Assert.Empty(none.Older);
        Assert.Empty(none.Opening);
        Assert.Equal(12, none.Recent.Count);
        Assert.False(none.HasOlderTurns);
    }

    [Fact]
    public void Split_OnlyTheOpeningPairsOlder_IsNothingToCompact()
    {
        // A first message whose reply was cut before it came: user, the two pairs, then the next turn.
        var messages = new List<ChatMessage>
        {
            Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_current_time"), Result(NeonSidekick.Llm.Assistant.OpeningClockCallId, "Friday"),
            User("two"), Assistant("reply"),
        };

        var plan = ConversationCompactor.Split(messages, keepRecent: 1);

        Assert.Equal(2, plan.Older.Count);
        Assert.Equal(2, plan.Opening.Count);
        Assert.False(plan.HasOlderTurns);
    }

    [Fact]
    public void Split_CopiesNeverViews()
    {
        var messages = ThreeTurns();
        var plan = ConversationCompactor.Split(messages, keepRecent: 1);
        messages.Clear();

        Assert.Equal(10, plan.Older.Count);
        Assert.Equal(2, plan.Recent.Count);
    }

    [Fact]
    public void Prune_StubsLongResultsInTheOlderTurns_NewObjects_TheHeldOnesUntouched()
    {
        var messages = ThreeTurns();
        var plan = ConversationCompactor.Split(messages, keepRecent: 1);

        var (pruned, count) = ConversationCompactor.Prune(plan);

        Assert.Equal(1, count);
        Assert.Equal(12, pruned.Count);
        var stub = Assert.IsType<FunctionResultContent>(pruned[8].Contents[0]);
        Assert.Equal("c1", stub.CallId);
        Assert.Equal("(a 500-character result, pruned by /compact)", stub.Result);
        Assert.NotSame(messages[8], pruned[8]);
        Assert.Equal(Long(500), ((FunctionResultContent)messages[8].Contents[0]).Result);   // the original list is as it was
        Assert.Same(messages[2], pruned[2]);   // the opening results stay, short or not
        Assert.Same(messages[10], pruned[10]); // the recent turn as it was
    }

    /// <summary>The older turns' replies lose their thinking with a prune (2026-09-28), uncounted; the kept turn keeps its own.</summary>
    [Fact]
    public void Prune_DropsTheOlderTurnsThinking_NotTheRecentOnes()
    {
        var messages = ThreeTurns();
        messages[9] = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("old"), new TextContent("reply two")]);
        messages[11] = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("new"), new TextContent("reply three")]);

        var (pruned, count) = ConversationCompactor.Prune(ConversationCompactor.Split(messages, keepRecent: 1));

        Assert.Equal(1, count);   // the long result alone
        Assert.Empty(pruned[9].Contents.OfType<TextReasoningContent>());
        Assert.Equal("reply two", pruned[9].Text);
        Assert.Single(messages[9].Contents.OfType<TextReasoningContent>());   // the held message untouched
        Assert.Same(messages[11], pruned[11]);
    }

    [Fact]
    public void Prune_LogsAnEntryPerStub_NamedByItsCall_PicturesCounted()
    {
        // The entries (2026-09-21, LLM compact show summary): the tool from the call that shares the result's id, the length; a carrier's pictures as one entry.
        var entries = new List<ConversationCompactor.PrunedEntry>();
        var (_, count) = ConversationCompactor.Prune(ConversationCompactor.Split(ThreeTurns(), keepRecent: 1), entries: entries);
        Assert.Equal(1, count);
        Assert.Equal([new ConversationCompactor.PrunedEntry("read_file", 500)], entries);

        entries.Clear();
        (_, count) = ConversationCompactor.Prune(ConversationCompactor.Split(TurnsWithACarrier(), keepRecent: 1), entries: entries);
        Assert.Equal(2, count);
        Assert.Equal([new ConversationCompactor.PrunedEntry("view_image", 0, 2)], entries);

        // A result whose call is not in the list is named the unknown tool; nothing logged without a list.
        entries.Clear();
        var orphan = ConversationCompactor.Split([User("one"), Assistant("r"), Result("c9", Long(300)), User("two"), Assistant("r")], keepRecent: 1);
        (_, count) = ConversationCompactor.Prune(orphan, entries: entries);
        Assert.Equal(1, count);
        Assert.Equal([new ConversationCompactor.PrunedEntry(ConversationCompactor.UnknownTool, 300)], entries);
        Assert.Equal(new Dictionary<string, string> { ["c1"] = "read_file", [NeonSidekick.Llm.Assistant.OpeningClockCallId] = "get_current_time", [NeonSidekick.Llm.Assistant.OpeningCwdCallId] = "get_working_directory" }, ConversationCompactor.CallNames(ThreeTurns()));
    }

    /// <summary>Two turns, the first with a viewed picture: the carrier sits between the result and the reply.</summary>
    private static List<ChatMessage> TurnsWithACarrier()
    {
        var history = new ConversationHistory("s");
        history.AddUser("one");
        history.AddMessage(Call("c1", "view_image"));
        history.AddToolResults([new FunctionResultContent("c1", "pic.png (4×4 image/png, 1 KB): next")]);
        history.AddToolImages([new NeonSidekick.Files.ImageAttachment("pic.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Png, 4, 4), new NeonSidekick.Files.ImageAttachment("b.jpg", [4], NeonSidekick.Files.ImageFile.Jpeg, 1, 1)]);
        history.AddAssistant("reply one");
        history.AddUser("two");
        history.AddAssistant("reply two");
        return history.Messages.ToList();
    }

    [Fact]
    public void Split_DoesNotCutAtACarrier()
    {
        var plan = ConversationCompactor.Split(TurnsWithACarrier(), keepRecent: 1);

        Assert.Equal("two", plan.Recent[0].Text);
        Assert.Equal(new[] { "user", "assistant", "tool", "user", "assistant" }, Roles(plan.Older));
        Assert.True(ConversationHistory.IsImageCarrier(plan.Older[3]));
    }

    [Fact]
    public void Prune_StubsACarriersPictures_KeepsTheTag_CountsEachPicture()
    {
        var messages = TurnsWithACarrier();
        var plan = ConversationCompactor.Split(messages, keepRecent: 1);

        var (pruned, count) = ConversationCompactor.Prune(plan);

        Assert.Equal(2, count);
        Assert.Equal(7, pruned.Count);
        var stub = pruned[3];
        Assert.NotSame(messages[3], stub);
        Assert.True(ConversationHistory.IsImageCarrier(stub));
        Assert.Equal(ConversationCompactor.PrunedImageStub(2), stub.Text);
        Assert.Empty(stub.Contents.OfType<DataContent>());
        Assert.Equal(2, messages[3].Contents.OfType<DataContent>().Count());   // the held one untouched
        Assert.Same(messages[2], pruned[2]);   // a short result stays

        // A stub is already pictureless: pruning again counts nothing and keeps it.
        var (again, none) = ConversationCompactor.Prune(ConversationCompactor.Split(pruned, keepRecent: 1));
        Assert.Equal(0, none);
        Assert.Same(stub, again[3]);
    }

    /// <summary>2026-10-03: a carrier's stub credits the tool its pictures came from, and keeps that source for the next prune and the session.</summary>
    [Fact]
    public void Prune_ACarrierFromGenerateImage_CreditsIt()
    {
        var history = new ConversationHistory("s");
        history.AddUser("one");
        history.AddToolImages([new NeonSidekick.Files.ImageAttachment("comfy\\a.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Jpeg, 4, 4)], "generate_image");
        history.AddAssistant("reply one");
        history.AddUser("two");
        var entries = new List<ConversationCompactor.PrunedEntry>();

        var (pruned, count) = ConversationCompactor.Prune(ConversationCompactor.Split(history.Messages.ToList(), keepRecent: 1), entries: entries);

        Assert.Equal(1, count);
        Assert.Equal(ConversationCompactor.PrunedImageStub(1, "generate_image"), pruned[1].Text);
        Assert.Equal("generate_image", ConversationHistory.CarrierSource(pruned[1]));
        Assert.Equal([new ConversationCompactor.PrunedEntry("generate_image", 0, 1)], entries);
    }

    /// <summary>2026-10-03: an older turn's own pictures (pasted, /imagine's, the camera's) go too, each a line naming its file, the text kept.</summary>
    [Fact]
    public void Prune_AnOlderTurnsOwnPictures_BecomeTheirLines_TheTextKept()
    {
        var history = new ConversationHistory("s");
        history.AddUser("what is this [Image #1]", [new NeonSidekick.Files.ImageAttachment("cat.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Png, 4, 4)]);
        history.AddAssistant("a cat");
        history.AddUser("and this [Image #1]", [new NeonSidekick.Files.ImageAttachment("dog.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Png, 4, 4)]);
        var entries = new List<ConversationCompactor.PrunedEntry>();

        var (pruned, count) = ConversationCompactor.Prune(ConversationCompactor.Split(history.Messages.ToList(), keepRecent: 1), entries: entries);

        Assert.Equal(1, count);
        Assert.Equal(["what is this [Image #1]", NeonSidekick.Llm.PictureBudget.LeftOut("cat.png")], pruned[0].Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Empty(pruned[0].Contents.OfType<DataContent>());
        Assert.False(ConversationHistory.IsImageCarrier(pruned[0]));
        Assert.Single(pruned[2].Contents.OfType<DataContent>());   // the recent turn's picture stays
        Assert.Equal([new ConversationCompactor.PrunedEntry(ConversationCompactor.UserPictureEntry, 0, 1)], entries);
    }

    [Fact]
    public void Prune_LeavesShortResults_AndTheOpeningPairs_WhateverTheirLength()
    {
        var messages = new List<ChatMessage>
        {
            User("one"),
            Call(NeonSidekick.Llm.Assistant.OpeningCwdCallId, "get_working_directory"), Result(NeonSidekick.Llm.Assistant.OpeningCwdCallId, Long(300)),
            Call("c1", "list_files"), Result("c1", Long(ConversationCompactor.PruneThreshold)),
            Assistant("reply"),
            User("two"), Assistant("reply two"),
        };

        var (pruned, count) = ConversationCompactor.Prune(ConversationCompactor.Split(messages, keepRecent: 1));

        Assert.Equal(0, count);
        Assert.All(pruned.Select((m, i) => (m, i)), pair => Assert.Same(messages[pair.i], pair.m));
    }

    [Fact]
    public void Prune_SeveralResultsInOneToolMessage_StubsEachLongOne()
    {
        var messages = new List<ChatMessage>
        {
            User("one"),
            new(ChatRole.Assistant, [new FunctionCallContent("a", "read_file", null), new FunctionCallContent("b", "read_file", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("a", Long(201)), new FunctionResultContent("b", "short")]),
            Assistant("reply"),
            User("two"), Assistant("reply two"),
        };

        var (pruned, count) = ConversationCompactor.Prune(ConversationCompactor.Split(messages, keepRecent: 1));

        Assert.Equal(1, count);
        Assert.Equal("(a 201-character result, pruned by /compact)", ((FunctionResultContent)pruned[2].Contents[0]).Result);
        Assert.Equal("short", ((FunctionResultContent)pruned[2].Contents[1]).Result);
        Assert.Equal("b", ((FunctionResultContent)pruned[2].Contents[1]).CallId);
    }

    [Fact]
    public void Summarised_IsTheSummaryAsAUserMessage_TheOpeningPairs_ThenTheRecentTurns()
    {
        var plan = ConversationCompactor.Split(ThreeTurns(), keepRecent: 1);

        var messages = ConversationCompactor.Summarised("  The user asked about one and two.  ", plan);

        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "tool", "user", "assistant" }, Roles(messages));
        Assert.Equal(ConversationCompactor.SummaryPreamble + "The user asked about one and two.", messages[0].Text);
        Assert.Equal("The conversation so far was compacted into this summary; continue as if you remembered it:\n\n", ConversationCompactor.SummaryPreamble);
        Assert.Equal("three", messages[5].Text);
    }

    [Fact]
    public void TheMemoryPair_IsAnOpeningPair_KeptBySummarise_AndNeverPruned()
    {
        // The third opening call (2026-09-17): listed with the other two, carried over a summary, its long result left alone.
        var messages = new List<ChatMessage>
        {
            User("one"),
            Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_current_time"), Result(NeonSidekick.Llm.Assistant.OpeningClockCallId, "Friday"),
            Call(NeonSidekick.Llm.Assistant.OpeningCwdCallId, "get_working_directory"), Result(NeonSidekick.Llm.Assistant.OpeningCwdCallId, @"D:\files"),
            Call(NeonSidekick.Llm.Assistant.OpeningMemoryCallId, "recall_memory"), Result(NeonSidekick.Llm.Assistant.OpeningMemoryCallId, Long(ConversationCompactor.PruneThreshold + 1)),
            Assistant("reply one"),
            User("two"), Assistant("reply two"),
        };

        var plan = ConversationCompactor.Split(messages, keepRecent: 1);
        Assert.Equal(6, plan.Opening.Count);
        Assert.Same(messages[6], plan.Opening[5]);

        var (pruned, count) = ConversationCompactor.Prune(plan);
        Assert.Equal(0, count);
        Assert.Same(messages[6], pruned[6]);

        var summarised = ConversationCompactor.Summarised("Summary.", plan);
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "tool", "assistant", "tool", "user", "assistant" }, Roles(summarised));
        Assert.Equal(NeonSidekick.Llm.Assistant.OpeningMemoryCallId, ((FunctionResultContent)summarised[6].Contents[0]).CallId);
    }

    [Fact]
    public void Wording_IsPinned()
    {
        Assert.Equal("Summarize the conversation above.", ConversationCompactor.SummaryRequest(null));
        Assert.Equal("Summarize the conversation above.", ConversationCompactor.SummaryRequest("  "));
        Assert.Equal("Summarize the conversation above. Pay particular attention to: the file list", ConversationCompactor.SummaryRequest(" the file list "));
        Assert.Equal("(a 4,312-character result, pruned by /compact)", ConversationCompactor.PrunedStub(4312));
        Assert.Equal("(a picture from view_image, pruned by /compact)", ConversationCompactor.PrunedImageStub(1));
        Assert.Equal("(2 pictures from view_image, pruned by /compact)", ConversationCompactor.PrunedImageStub(2));
        Assert.Equal("(a picture from generate_image, pruned by /compact)", ConversationCompactor.PrunedImageStub(1, "generate_image"));
        Assert.Equal("you", ConversationCompactor.UserPictureEntry);
        Assert.StartsWith("You are compacting a conversation between a user and Neon", ConversationCompactor.SummaryInstruction);
        Assert.Contains("do not mention that this is a summary", ConversationCompactor.SummaryInstruction);
        Assert.Equal(200, ConversationCompactor.PruneThreshold);
    }

    [Theory]
    [InlineData(8000, 10000, 80, true)]     // at the share
    [InlineData(8001, 10000, 80, true)]
    [InlineData(7999, 10000, 80, false)]
    [InlineData(9999, 10000, 100, false)]
    [InlineData(10000, 10000, 100, true)]
    [InlineData(9000, 10000, 0, false)]     // off
    [InlineData(0, 10000, 80, false)]       // nothing sent yet, or just compacted
    public void ShouldAutoCompact_AtOrPastTheShare_OfAKnownWindow(long inUse, int window, int percent, bool expected)
    {
        var last = new TokenUsage(inUse, 0, inUse, 1, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(expected, ConversationCompactor.ShouldAutoCompact(last, new ContextLength(window, "test"), percent));
    }

    [Fact]
    public void ShouldAutoCompact_NeverWithoutAWindow()
    {
        var last = new TokenUsage(9000, 0, 9000, 1, TimeSpan.Zero, TimeSpan.Zero);
        Assert.False(ConversationCompactor.ShouldAutoCompact(last, null, 10));
        Assert.False(ConversationCompactor.ShouldAutoCompact(last, new ContextLength(0, "test"), 10));
    }

    // ── RunAsync ────────────────────────────────────────────────────────────

    private static (FakeChatClient Client, NeonSidekick.Llm.Assistant Assistant, TokenTally Tally) Build(IEnumerable<ChatMessage> messages)
    {
        var client = new FakeChatClient();
        var history = new ConversationHistory("sys");
        history.Replace(messages.ToList());
        return (client, new NeonSidekick.Llm.Assistant(client, history, Timeouts), new TokenTally());
    }

    [Fact]
    public async Task RunAsync_Summary_SwapsTheHistory_BillsTheTally_AndReportsTheCounts()
    {
        var (client, assistant, tally) = Build(ThreeTurns());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        client.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(3900, 50));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: "the file", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(12, result.MessagesBefore);
        Assert.Equal(7, result.MessagesAfter);
        Assert.Equal(0, result.Pruned);
        Assert.Equal(4, result.OpeningKept);                        // the two opening pairs
        Assert.Equal(2, result.RecentKept);                         // the kept turn: its user message and reply
        Assert.Equal(3900, result.Usage!.Value.Input);
        Assert.Equal(50, result.Usage.Value.Output);
        Assert.Equal("A summary.", result.Summary);   // carried for LLM compact show summary (2026-09-21)
        Assert.Empty(result.Entries);

        // The request: the instruction, the older turns as they were, the focused request last; no tools, thinking off.
        var request = Assert.Single(client.Requests);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal(ConversationCompactor.SummaryInstruction, request[0].Text);
        Assert.Equal(12, request.Count);
        Assert.Equal("one", request[1].Text);
        Assert.Equal(ConversationCompactor.SummaryRequest("the file"), request[^1].Text);
        Assert.Null(client.Options[0]!.Tools);
        Assert.Equal(ReasoningEffort.None, client.Options[0]!.Reasoning!.Effort);

        // The history: the summary, the pairs, the recent turn; the turn count keeps the opening calls from re-seeding.
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "tool", "user", "assistant" }, Roles(assistant.History.Messages));
        Assert.Equal(2, assistant.History.TurnCount);
        Assert.True(assistant.History.TryReplaceToolResult(NeonSidekick.Llm.Assistant.OpeningCwdCallId, @"E:\other"));

        // The tally: billed to the conversation and the session, the context in use zeroed.
        Assert.Equal(2, tally.Conversation.Requests);
        Assert.Equal(4000 + 3900, tally.Conversation.Input);
        Assert.Equal(2, tally.Session.Requests);
        Assert.True(tally.LastRequest.IsEmpty);
    }

    [Fact]
    public async Task RunAsync_Prune_NoRequest_StubsAndReportsTheCount()
    {
        var (client, assistant, tally) = Build(ThreeTurns());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.Pruned);
        Assert.Equal(12, result.MessagesBefore);
        Assert.Equal(12, result.MessagesAfter);
        Assert.Equal(4, result.OpeningKept);
        Assert.Equal(2, result.RecentKept);
        Assert.Null(result.Usage);
        Assert.Null(result.Summary);
        Assert.Equal([new ConversationCompactor.PrunedEntry("read_file", 500)], result.Entries);
        Assert.Empty(client.Requests);
        Assert.Equal("(a 500-character result, pruned by /compact)", ((FunctionResultContent)assistant.History.Messages[8].Contents[0]).Result);
        Assert.Equal(1, tally.Conversation.Requests);
        Assert.True(tally.LastRequest.IsEmpty);
    }

    [Fact]
    public async Task RunAsync_NothingOlder_IsNull_AndTouchesNothing()
    {
        var (client, assistant, tally) = Build(ThreeTurns());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        Assert.Null(await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 3, focus: null, CancellationToken.None));
        Assert.Null(await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 2, focus: null, CancellationToken.None));   // the older turn holds no long result

        Assert.Empty(client.Requests);
        Assert.Equal(12, assistant.History.Messages.Count);
        Assert.Equal(4100, tally.LastRequest.Total);
    }

    [Fact]
    public async Task RunAsync_Summary_FailsOrIsCancelled_LeavesTheHistoryAndTheTally()
    {
        var (client, assistant, tally) = Build(ThreeTurns());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        client.Enqueue(FakeChatClient.Text("half"));
        client.ThrowAt = 1;

        await Assert.ThrowsAsync<HttpRequestException>(() => ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        client.ThrowAt = int.MaxValue;
        client.Enqueue(FakeChatClient.Text("half"), FakeChatClient.Text(" more"));
        client.BeforeUpdate = (i, _) => { if (i == 1) cts.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, cts.Token));

        Assert.Equal(12, assistant.History.Messages.Count);
        Assert.Equal("one", assistant.History.Messages[0].Text);
        Assert.Equal(4100, tally.LastRequest.Total);
        Assert.Equal(1, tally.Conversation.Requests);
    }

    [Fact]
    public async Task RunAsync_Summary_EmptyAnswer_Throws_TheHistoryUntouched()
    {
        var (client, assistant, tally) = Build(ThreeTurns());
        client.Enqueue(FakeChatClient.Text("  ")).Enqueue(FakeChatClient.Text("  "));   // asked for twice (2026-09-28)

        var ex = await Assert.ThrowsAsync<EmptySummaryException>(() => ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, CancellationToken.None));

        Assert.Equal(NeonSidekick.Llm.Assistant.EmptySummaryError, ex.Message);
        Assert.Equal(12, assistant.History.Messages.Count);
    }

    // ── LeanTranscript: the summariser's second try (2026-09-28) ──

    [Fact]
    public void LeanTranscript_IsAPlainDialogue_TheResultsCut_TheRolesAlternating()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "look at these"),
            new(ChatRole.Assistant, [new TextReasoningContent("which first?"), new TextContent("Reading both."), new FunctionCallContent("c1", "read_file"), new FunctionCallContent("c2", "view_image")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "short")]),
            new(ChatRole.Tool, [new FunctionResultContent("c2", "abcdefghij")]),
            new(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
            new(ChatRole.Tool, [new FunctionResultContent("gone", "orphan")]),
            new(ChatRole.Assistant, [new TextReasoningContent("only thinking")]),
            new(ChatRole.Assistant, "Both read."),
        };

        var lean = ConversationCompactor.LeanTranscript(messages, maxResultChars: 5);

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant }, lean.Select(m => m.Role));
        Assert.Equal("look at these", lean[0].Text);
        Assert.Equal("Reading both.\n[called read_file {}]\n[called view_image {}]", lean[1].Text);
        Assert.Equal(
            "[read_file returned] short\n\n[view_image returned] abcde" + ConversationCompactor.LeanCut(5) + "\n\n" + ConversationCompactor.LeanPicture + "\n\n[" + ConversationCompactor.UnknownTool + " returned] orpha" + ConversationCompactor.LeanCut(1),
            lean[2].Text);
        Assert.Equal("Both read.", lean[3].Text);   // the thinking-only message dropped, its neighbour kept
        Assert.All(lean, m => Assert.IsType<TextContent>(Assert.Single(m.Contents)));
    }

    // ── PruneRecent: the last turn's older iterations (the mid-turn guard, the automatic compact) ──

    /// <summary>Two turns; the second is a long tool loop as the chef session was: three iterations, the second with a picture, the third's results the model has not read yet.</summary>
    private static List<ChatMessage> ALongLastTurn(bool firstTurnReads = false)
    {
        var history = new ConversationHistory("s");
        history.AddUser("one");
        history.AddMessage(Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_current_time"));
        history.AddToolResults([new FunctionResultContent(NeonSidekick.Llm.Assistant.OpeningClockCallId, Long(300))]);
        if (firstTurnReads)
        {
            history.AddMessage(Call("c0", "read_file"));
            history.AddToolResults([new FunctionResultContent("c0", Long(500))]);
        }

        history.AddAssistant("reply one");
        history.AddUser("two");
        history.AddMessage(Call("c1", "web_fetch"));
        history.AddToolResults([new FunctionResultContent("c1", Long(900)), new FunctionResultContent("c1b", "short")]);
        history.AddMessage(Call("c2", "view_image"));
        history.AddToolResults([new FunctionResultContent("c2", Long(400))]);
        history.AddToolImages([new NeonSidekick.Files.ImageAttachment("pic.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Png, 4, 4)]);
        history.AddMessage(Call("c3", "web_fetch"));
        history.AddToolResults([new FunctionResultContent("c3", Long(700))]);
        return history.Messages.ToList();
    }

    [Fact]
    public void PruneRecent_StubsTheLastTurnsOlderIterations_KeepsTheLastOne_AndEverythingBefore()
    {
        var messages = ALongLastTurn();

        var (pruned, count) = ConversationCompactor.PruneRecent(messages);

        Assert.Equal(3, count);                                     // c1, c2 and the picture; c1b is short
        Assert.Equal(messages.Count, pruned.Count);
        Assert.Equal(Roles(messages), Roles(pruned));
        for (int i = 0; i < 4; i++)
        {
            Assert.Same(messages[i], pruned[i]);                    // the first turn, the opening pair's long result included
        }

        Assert.Same(messages[4], pruned[4]);                        // the user message
        Assert.Same(messages[5], pruned[5]);                        // the call
        Assert.Equal("(a 900-character result, pruned by /compact)", ((FunctionResultContent)pruned[6].Contents[0]).Result);
        Assert.Equal("short", ((FunctionResultContent)pruned[6].Contents[1]).Result);
        Assert.Equal("(a 400-character result, pruned by /compact)", ((FunctionResultContent)pruned[8].Contents[0]).Result);
        Assert.True(ConversationHistory.IsImageCarrier(pruned[9]));
        Assert.Equal(ConversationCompactor.PrunedImageStub(1), pruned[9].Text);
        Assert.DoesNotContain(pruned[9].Contents, c => c is DataContent);
        Assert.Same(messages[10], pruned[10]);                      // the last iteration: its call...
        Assert.Same(messages[11], pruned[11]);                      // ...and its result, unread by the model, verbatim
        Assert.Equal(Long(700), ((FunctionResultContent)messages[11].Contents[0]).Result);
        Assert.Equal(Long(900), ((FunctionResultContent)messages[6].Contents[0]).Result);   // the held list untouched
    }

    private static ChatMessage CallWith(string callId, string name, params (string Key, object? Value)[] arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments.ToDictionary(a => a.Key, a => a.Value))]);

    /// <summary>
    /// Long call arguments are stubbed as long results are (2026-10-17, the toki-pona session: two 27 KB write_file bodies and the skill
    /// bodies stayed while their results were pruned). The session's shape in one turn: the earlier iterations' long arguments go, keys
    /// and short ones kept, a raw SDK message dropped with them; the last iteration's call, the one being answered, is never touched.
    /// </summary>
    [Fact]
    public void PruneRecent_StubsTheEarlierIterationsLongArguments_NeverTheLastCall()
    {
        var history = new ConversationHistory("s");
        history.AddUser("write the vocabulary");
        var write = CallWith("w1", "skill_editor", ("action", "write_file"), ("name", "toki-pona"), ("path", "references/vocabulary.txt"), ("content", Long(27_610)));
        write.RawRepresentation = new object();
        history.AddMessage(write);
        history.AddToolResults([new FunctionResultContent("w1", "skill 'toki-pona' (profile): wrote references\\vocabulary.txt")]);
        history.AddMessage(CallWith("p1", "present_plan", ("title", "t"), ("markdown", Long(5_000))));
        history.AddToolResults([new FunctionResultContent("p1", "approved")]);
        history.AddMessage(CallWith("w2", "write_file", ("path", "b.txt"), ("content", Long(3_000))));
        history.AddToolResults([new FunctionResultContent("w2", "wrote b.txt")]);
        var messages = history.Messages.ToList();
        var entries = new List<ConversationCompactor.PrunedEntry>();

        var (pruned, count) = ConversationCompactor.PruneRecent(messages, entries: entries);

        Assert.Equal(0, count);   // no result was long: the arguments alone are this prune
        var stubbed = (FunctionCallContent)pruned[1].Contents[0];
        Assert.Equal("(a 27,610-character argument, pruned by /compact)", stubbed.Arguments!["content"]);
        Assert.Equal("references/vocabulary.txt", stubbed.Arguments["path"]);
        Assert.Equal("write_file", stubbed.Arguments["action"]);
        Assert.Equal("w1", stubbed.CallId);
        Assert.Null(pruned[1].RawRepresentation);
        Assert.Same(messages[3], pruned[3]);   // present_plan's markdown is kept: an approved plan runs from it
        Assert.Same(messages[5], pruned[5]);   // the last iteration's call, long content and all
        Assert.Equal([new ConversationCompactor.PrunedEntry("skill_editor", 27_610, Argument: "content")], entries);
        Assert.Equal(Long(27_610), ((FunctionCallContent)messages[1].Contents[0]).Arguments!["content"]);   // the held list untouched
        Assert.True(ConversationCompactor.EstimateTokens(pruned) < ConversationCompactor.EstimateTokens(messages) - 6_000);
        Assert.Equal(["present_plan"], ConversationCompactor.KeepsArguments);
    }

    /// <summary>A prune of an older turn stubs its long arguments too, the thinking dropped from the same clone; short arguments and the opening calls stay.</summary>
    [Fact]
    public async Task RunAsync_Prune_StubsAnOlderTurnsLongArguments_AndCountsThem()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("thinking"), new FunctionCallContent("c1", "write_file", new Dictionary<string, object?> { ["path"] = "a.txt", ["content"] = Long(4_000) })]);
        var (_, assistant, tally) = Build([User("one"), call, Result("c1", "wrote a.txt"), Assistant("done"), User("two"), Assistant("reply two")]);

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.Pruned);
        Assert.Equal(1, result.ArgumentsPruned);
        var kept = assistant.History.Messages[1];
        Assert.DoesNotContain(kept.Contents, c => c is TextReasoningContent);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(4_000), ((FunctionCallContent)kept.Contents.Single()).Arguments!["content"]);
        Assert.Equal("a.txt", ((FunctionCallContent)kept.Contents.Single()).Arguments!["path"]);
        Assert.Equal("(✂️ write_file content · 4,000 characters)", NeonSidekick.App.CompactionText.PrunedLine(result.Entries.Single()));
    }

    [Fact]
    public void PrunedStubs_AreRecognized_AndNothingElse()
    {
        Assert.True(ConversationCompactor.IsPrunedStub(ConversationCompactor.PrunedArgumentStub(27_610)));
        Assert.True(ConversationCompactor.IsPrunedStub(" " + ConversationCompactor.PrunedStub(4_312) + "\n"));
        Assert.False(ConversationCompactor.IsPrunedStub("The stub reads (a 12-character result, pruned by /compact) in the log."));
        Assert.False(ConversationCompactor.IsPrunedStub(""));
        Assert.False(ConversationCompactor.IsPrunedStub(null));
        Assert.Equal("(a 27,610-character argument, pruned by /compact)", ConversationCompactor.PrunedArgumentStub(27_610));
        Assert.Equal("3 tool results and 2 call arguments", NeonSidekick.Llm.Assistant.PrunedWords(3, 2));
        Assert.Equal("1 call argument", NeonSidekick.Llm.Assistant.PrunedWords(0, 1));
        Assert.Equal("(✂️ context at 85%: pruned 23 tool results from this turn)", NeonSidekick.Llm.Assistant.TurnPrunedNotice(85, 23));
        Assert.Equal("(✂️ context at 94%: pruned 11 tool results and 2 call arguments from this turn)", NeonSidekick.Llm.Assistant.TurnPrunedNotice(94, 11, 2));
    }

    [Fact]
    public void PruneRecent_NothingToStub_IsACopyAndZero()
    {
        var one = ThreeTurns();                                     // the last turn has no tool message
        var (copy, count) = ConversationCompactor.PruneRecent(one);
        Assert.Equal(0, count);
        Assert.Equal(one, copy);
        Assert.NotSame(one, copy);

        var single = new List<ChatMessage> { User("two"), Call("c1", "read_file"), Result("c1", Long(500)) };   // one iteration only: it is the last
        Assert.Equal(0, ConversationCompactor.PruneRecent(single).Pruned);

        Assert.Equal(0, ConversationCompactor.PruneRecent([Assistant("no turn at all")]).Pruned);
        Assert.Equal(0, ConversationCompactor.PruneRecent([]).Pruned);
    }

    [Fact]
    public void PruneRecent_APictureAfterTheLastResult_StaysWithIt()
    {
        var history = new ConversationHistory("s");
        history.AddUser("one");
        history.AddMessage(Call("c1", "read_file"));
        history.AddToolResults([new FunctionResultContent("c1", Long(500))]);
        history.AddMessage(Call("c2", "view_image"));
        history.AddToolResults([new FunctionResultContent("c2", "pic")]);
        history.AddToolImages([new NeonSidekick.Files.ImageAttachment("pic.png", [1, 2, 3], NeonSidekick.Files.ImageFile.Png, 4, 4)]);

        var (pruned, count) = ConversationCompactor.PruneRecent(history.Messages);

        Assert.Equal(1, count);
        Assert.Contains(pruned[^1].Contents, c => c is DataContent);
    }

    [Fact]
    public async Task RunAsync_PruneRecent_Summary_StubsTheKeptTurnsToo_AndReportsBoth()
    {
        var (client, assistant, tally) = Build(ALongLastTurn());
        client.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(300, 20));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, CancellationToken.None, pruneRecent: true);

        Assert.NotNull(result);
        Assert.True(result.Summarised);
        Assert.Equal(3, result.Pruned);
        Assert.Equal(12, result.MessagesBefore);
        Assert.Equal(11, result.MessagesAfter);                     // the summary + the opening pair + the 8 of the kept turn
        Assert.Equal(2, result.OpeningKept);
        Assert.Equal(8, result.RecentKept);                         // kept in place, three of its results stubbed
        Assert.Single(client.Requests);
        var kept = assistant.History.Messages;
        Assert.Equal("(a 900-character result, pruned by /compact)", ((FunctionResultContent)kept[5].Contents[0]).Result);
        Assert.Equal(Long(700), ((FunctionResultContent)kept[^1].Contents[0]).Result);
    }

    [Fact]
    public async Task RunAsync_PruneRecent_NothingOlder_IsAPruneAlone_NoRequest()
    {
        var (client, assistant, tally) = Build(ALongLastTurn());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 2, focus: null, CancellationToken.None, pruneRecent: true);

        Assert.NotNull(result);
        Assert.False(result.Summarised);
        Assert.Equal(3, result.Pruned);
        Assert.Null(result.Usage);
        Assert.Equal(12, result.MessagesAfter);
        Assert.Equal(0, result.OpeningKept);                        // nothing older: the opening pair is in the kept turns
        Assert.Equal(12, result.RecentKept);
        Assert.Empty(client.Requests);                              // never the summariser over the history that just failed
        Assert.True(tally.LastRequest.IsEmpty);
        Assert.Equal("(a 900-character result, pruned by /compact)", ((FunctionResultContent)assistant.History.Messages[6].Contents[0]).Result);

        // By hand, the same history is nothing to compact.
        Assert.Null(await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 2, focus: null, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_PruneRecent_PruneMode_CountsBoth()
    {
        var (client, assistant, tally) = Build(ALongLastTurn(firstTurnReads: true));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None, pruneRecent: true);

        Assert.NotNull(result);
        Assert.False(result.Summarised);
        Assert.Equal(4, result.Pruned);                             // the first turn's read (the opening pair's long result never) + the kept turn's three
        Assert.Empty(client.Requests);
        Assert.Equal("(a 500-character result, pruned by /compact)", ((FunctionResultContent)assistant.History.Messages[4].Contents[0]).Result);
        Assert.Equal("(a 900-character result, pruned by /compact)", ((FunctionResultContent)assistant.History.Messages[8].Contents[0]).Result);
    }

    [Fact]
    public async Task RunAsync_WithoutPruneRecent_KeepsTheRecentTurnsVerbatim()
    {
        var (client, assistant, tally) = Build(ALongLastTurn());
        client.Enqueue(FakeChatClient.Text("A summary."));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result.Pruned);
        Assert.Equal(Long(900), ((FunctionResultContent)assistant.History.Messages[5].Contents[0]).Result);
    }

    // ── The loaded skills (2026-09-16): Skill compact mode ──────────────────

    /// <summary>A loaded skill's result as the turn loop tags it (<see cref="NeonSidekick.Llm.Assistant.ResultContent"/>).</summary>
    private static ChatMessage SkillResult(string callId, string text) =>
        new(ChatRole.Tool, [NeonSidekick.Llm.Assistant.ResultContent(new FunctionCallContent(callId, NeonSidekick.Llm.Tools.LoadSkillTool.ToolName), text)]);

    /// <summary>Two turns: the first loads a skill (by the model) and reads a file, the second loads one by /skill and reads a file, then the last iteration.</summary>
    private static List<ChatMessage> SkilledTurns() =>
    [
        User("one"),
        Call("s1", "load_skill"), SkillResult("s1", Long(600)),
        Call("c1", "read_file"), Result("c1", Long(500)),
        Assistant("reply one"),
        User("two"),
        Call("s2", "load_skill"), SkillResult("s2", Long(700)),
        Call("c2", "read_file"), Result("c2", Long(400)),
        Call("c3", "read_file"), Result("c3", Long(300)),
    ];

    [Fact]
    public void Prune_Protected_KeepsTheLoadedSkills_AndStubsTheRest()
    {
        var plan = ConversationCompactor.Split(SkilledTurns(), keepRecent: 1);

        var (pruned, count) = ConversationCompactor.Prune(plan);                 // protected is the default

        Assert.Equal(1, count);
        Assert.Equal(Long(600), ((FunctionResultContent)pruned[2].Contents[0]).Result);
        Assert.Equal("(a 500-character result, pruned by /compact)", ((FunctionResultContent)pruned[4].Contents[0]).Result);
        Assert.True(ConversationHistory.IsSkillResult((FunctionResultContent)pruned[2].Contents[0]));

        var (open, both) = ConversationCompactor.Prune(plan, protectSkills: false);
        Assert.Equal(2, both);
        Assert.Equal("(a 600-character result, pruned by /compact)", ((FunctionResultContent)open[2].Contents[0]).Result);
        Assert.False(ConversationHistory.IsSkillResult((FunctionResultContent)open[2].Contents[0]));
    }

    [Fact]
    public void PruneRecent_Protected_KeepsTheLoadedSkills_InTheLastTurnToo()
    {
        var messages = SkilledTurns();

        var (pruned, count) = ConversationCompactor.PruneRecent(messages);

        Assert.Equal(1, count);                                     // c2; the /skill pair is kept, c3 is the last iteration
        Assert.Equal(Long(700), ((FunctionResultContent)pruned[8].Contents[0]).Result);
        Assert.Equal("(a 400-character result, pruned by /compact)", ((FunctionResultContent)pruned[10].Contents[0]).Result);
        Assert.Equal(Long(300), ((FunctionResultContent)pruned[12].Contents[0]).Result);

        var (open, both) = ConversationCompactor.PruneRecent(messages, protectSkills: false);
        Assert.Equal(2, both);
        Assert.Equal("(a 700-character result, pruned by /compact)", ((FunctionResultContent)open[8].Contents[0]).Result);
    }

    [Fact]
    public async Task RunAsync_Prune_FollowsTheMode_ForTheOlderAndTheKeptTurns()
    {
        var (client, assistant, tally) = Build(SkilledTurns());
        tally.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        var kept = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None, pruneRecent: true);
        Assert.NotNull(kept);
        Assert.Equal(2, kept.Pruned);                               // c1 in the older turn, c2 in the kept one; both skills stay
        Assert.Equal(Long(600), ((FunctionResultContent)assistant.History.Messages[2].Contents[0]).Result);
        Assert.Equal(Long(700), ((FunctionResultContent)assistant.History.Messages[8].Contents[0]).Result);
        Assert.Empty(client.Requests);

        var (client2, assistant2, tally2) = Build(SkilledTurns());
        tally2.Add(new TokenUsage(4000, 100, 4100, 1, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        var open = await ConversationCompactor.RunAsync(assistant2, tally2, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None, pruneRecent: true, protectSkills: false);
        Assert.NotNull(open);
        Assert.Equal(4, open.Pruned);
        Assert.Equal("(a 600-character result, pruned by /compact)", ((FunctionResultContent)assistant2.History.Messages[2].Contents[0]).Result);
        Assert.Equal("(a 700-character result, pruned by /compact)", ((FunctionResultContent)assistant2.History.Messages[8].Contents[0]).Result);
        Assert.Empty(client2.Requests);
    }

    [Fact]
    public async Task MidTurnGuard_FollowsTheMode()
    {
        // The guard prunes this turn's older results after the iteration that tripped it: the loaded skill stays under protected, goes under unprotected.
        foreach (bool protect in new[] { true, false })
        {
            var load = new FixedTool(NeonSidekick.Llm.Tools.LoadSkillTool.ToolName, Long(600));
            var read = new FixedTool("read_file", Long(500));
            var client = new FakeChatClient();
            var history = new ConversationHistory("sys");
            var assistant = new NeonSidekick.Llm.Assistant(client, history, Timeouts, new AIFunction[] { load, read })
            {
                ContextGuard = new NeonSidekick.Llm.Assistant.TurnContextGuard(1000, 50, ToolCompactMode.Prune, protect),
            };
            client.Enqueue(FakeChatClient.Call("s1", NeonSidekick.Llm.Tools.LoadSkillTool.ToolName, new Dictionary<string, object?> { ["name"] = "haiku" }), FakeChatClient.Usage(100, 10));
            client.Enqueue(FakeChatClient.Call("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a" }), FakeChatClient.Usage(100, 10));
            client.Enqueue(FakeChatClient.Call("c2", "read_file", new Dictionary<string, object?> { ["path"] = "b" }), FakeChatClient.Usage(600, 10));
            client.EnqueueText("done");

            await foreach (var _ in assistant.RunTurnAsync("go")) { }

            var results = history.Messages.Where(m => m.Role == ChatRole.Tool).Select(m => (FunctionResultContent)m.Contents[0]).ToList();
            Assert.Equal(["s1", "c1", "c2"], results.Select(r => r.CallId));
            Assert.Equal(protect ? Long(600) : "(a 600-character result, pruned by /compact)", results[0].Result);
            Assert.Equal("(a 500-character result, pruned by /compact)", results[1].Result);
            Assert.Equal(Long(500), results[2].Result);
        }
    }

    // ── The mid-turn compact's helpers (2026-09-28) ───────────────────────────

    [Fact]
    public void EstimateTokens_CountsText_CallsAsTheirJson_Results_AndPictures_OverFour()
    {
        Assert.Equal(0, ConversationCompactor.EstimateTokens([]));
        Assert.Equal(2, ConversationCompactor.EstimateTokens([User(Long(8)), Assistant("x")]));            // 9 characters: 2
        // A call: its name and its arguments as the wire's JSON — echo + {"text":"abcd"} = 4 + 15.
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["text"] = "abcd" })]);
        Assert.Equal(19 / 4, ConversationCompactor.EstimateTokens([call]));
        Assert.Equal(100, ConversationCompactor.EstimateTokens([Result("c1", Long(400))]));
        var picture = new ChatMessage(ChatRole.User, [new TextContent(Long(40)), new DataContent(new byte[] { 1, 2, 3 }, "image/png"), new DataContent(new byte[] { 4 }, "image/png")]);
        Assert.Equal(10 + 2 * ConversationCompactor.PictureTokens, ConversationCompactor.EstimateTokens([picture]));
        Assert.Equal((4, 1000), (ConversationCompactor.CharsPerToken, ConversationCompactor.PictureTokens));
    }

    [Fact]
    public void SplitTurn_TheLastTurn_ItsOpeningPairs_EarlierIterations_AndTheLastIteration()
    {
        var messages = new List<ChatMessage>
        {
            User("earlier"), Assistant("sure"),
            User("go"),
            Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_time"), Result(NeonSidekick.Llm.Assistant.OpeningClockCallId, "noon"),
            Call("c1", "read_file"), Result("c1", Long(300)),
            Call("c2", "read_file"), Result("c2", Long(300)),
        };

        var plan = ConversationCompactor.SplitTurn(messages)!;

        Assert.Equal(["earlier", "sure"], plan.Before.Select(m => m.Text));
        Assert.Equal("go", plan.Start.Text);
        Assert.Equal(2, plan.Opening.Count);
        Assert.Equal(["assistant", "tool"], Roles(plan.Earlier));
        Assert.Equal("c1", Assert.Single(plan.Earlier[0].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("c2", Assert.Single(plan.Last[0].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal(2, plan.Last.Count);
        Assert.Equal(7, plan.Transcript.Count);                                          // everything ahead of c2
        Assert.True(plan.HasEarlierIterations);

        // One iteration only: nothing earlier. No call of the model's, or no turn: no plan.
        Assert.False(ConversationCompactor.SplitTurn([User("go"), Call("c1", "x"), Result("c1", "y")])!.HasEarlierIterations);
        Assert.Null(ConversationCompactor.SplitTurn([User("go"), Assistant("hi")]));
        Assert.Null(ConversationCompactor.SplitTurn([User("go"), Call(NeonSidekick.Llm.Assistant.OpeningCwdCallId, "cwd"), Result(NeonSidekick.Llm.Assistant.OpeningCwdCallId, "D:")]));
        Assert.Null(ConversationCompactor.SplitTurn([]));
    }

    [Fact]
    public void SummarisedTurn_TheNoteRidesTheUsersMessage_ReplacingAnEarlierOne_TheLastIterationUntouched()
    {
        var messages = new List<ChatMessage>
        {
            User("earlier"),
            User("go"),
            Call(NeonSidekick.Llm.Assistant.OpeningClockCallId, "get_time"), Result(NeonSidekick.Llm.Assistant.OpeningClockCallId, "noon"),
            Call("c1", "read_file"), Result("c1", Long(300)),
            Call("c2", "read_file"), Result("c2", Long(300)),
        };

        var once = ConversationCompactor.SummarisedTurn("  Read the first file.  ", ConversationCompactor.SplitTurn(messages)!);

        Assert.Equal(["user", "user", "assistant", "tool", "assistant", "tool"], Roles(once));
        Assert.Equal("earlier", once[0].Text);
        var parts = once[1].Contents.OfType<TextContent>().ToList();
        Assert.Equal(["go", ConversationCompactor.TurnProgressPreamble + "Read the first file."], parts.Select(t => t.Text));
        Assert.False(ConversationCompactor.IsTurnProgress(parts[0]));
        Assert.True(ConversationCompactor.IsTurnProgress(parts[1]));
        Assert.Same(messages[2], once[2]);                                               // the opening pair in place
        Assert.Same(messages[6], once[4]);                                               // the last iteration as it was
        Assert.Same(messages[7], once[5]);
        Assert.Equal("go", messages[1].Text);                                            // the held message untouched

        // A second compact of the same turn: the note replaced, not added.
        once.Add(Call("c3", "read_file"));
        once.Add(Result("c3", "short"));
        var twice = ConversationCompactor.SummarisedTurn("Read two files.", ConversationCompactor.SplitTurn(once)!);
        Assert.Equal(["go", ConversationCompactor.TurnProgressPreamble + "Read two files."], twice[1].Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Equal(["user", "user", "assistant", "tool", "assistant", "tool"], Roles(twice));
        Assert.Equal("\n\n(The work on this request so far was compacted into this progress note; carry on from it:)\n\n", ConversationCompactor.TurnProgressPreamble);
    }

    // ── Room for the next reply (2026-10-18) ───────────────────────────────

    [Theory]
    [InlineData(7500, 0, false)]          // 75 % of the window, under the 90 % share alone
    [InlineData(7500, 1500, true)]        // with room for a 1,500-token reply
    [InlineData(7500, 1499, false)]
    [InlineData(6400, 5000, false)]       // 5,000 capped at a quarter (2,500): 8,900
    [InlineData(6500, 5000, true)]        // 9,000
    [InlineData(0, 5000, false)]          // nothing sent yet, or just compacted: never, reserve or not
    public void ShouldAutoCompact_LeavesRoomForTheLargestReply(long inUse, long largestReply, bool expected)
    {
        var last = new TokenUsage(inUse, 0, inUse, 1, TimeSpan.Zero, TimeSpan.Zero);
        Assert.Equal(expected, ConversationCompactor.ShouldAutoCompact(last, new ContextLength(10_000, "test"), 90, largestReply));
    }

    [Fact]
    public void ReplyReserve_IsTheLargestReply_UpToAQuarterOfTheWindow()
    {
        Assert.Equal(25, ConversationCompactor.MaxReplyReservePercent);
        Assert.Equal(28_095, ConversationCompactor.ReplyReserve(28_095, 151_427));     // the gap chain's: under the cap
        Assert.Equal(37_856, ConversationCompactor.ReplyReserve(50_000, 151_427));     // a quarter of the window
        Assert.Equal(0, ConversationCompactor.ReplyReserve(0, 151_427));
    }

    // ── Superseded plan copies (2026-10-18) ────────────────────────────────

    private static ChatMessage Present(string callId, string markdown) =>
        CallWith(callId, NeonSidekick.Llm.Tools.PresentPlanTool.ToolName, ("title", "T"), ("markdown", markdown));

    private static ChatMessage ReadPlan(string callId, string path) =>
        CallWith(callId, NeonSidekick.Llm.Tools.ReadFileTool.ToolName, ("path", path));

    private static string ArgumentOf(ChatMessage message, string key) =>
        message.Contents.OfType<FunctionCallContent>().Single().Arguments![key]?.ToString()!;

    private static string ResultOf(ChatMessage message, string callId) =>
        (string)message.Contents.OfType<FunctionResultContent>().Single(r => r.CallId == callId).Result!;

    /// <summary>Three presentations and two plan reads: only the latest presentation and what follows it stay whole.</summary>
    [Fact]
    public void StubSupersededPlans_KeepsOnlyTheLatestPresentation_AndWhatFollows()
    {
        List<ChatMessage> messages =
        [
            User("plan it"),
            ReadPlan("r1", ".neon/plans/x.md"), Result("r1", Long(3000)),
            Present("p1", Long(3000)), Result("p1", "saved"),
            User("more"),
            Present("p2", Long(3100)), Result("p2", "saved"),
            ReadPlan("r2", ".neon/plans/x.md"), Result("r2", Long(3100)),
            Present("p3", Long(3200)), Result("p3", "saved"),
            Assistant("presented"),
        ];
        messages[3].RawRepresentation = new object();
        var entries = new List<ConversationCompactor.PrunedEntry>();

        var (stubbed, pruned, arguments) = ConversationCompactor.StubSupersededPlans(messages, entries);

        Assert.Equal((2, 2), (pruned, arguments));
        Assert.Equal(messages.Count, stubbed.Count);
        Assert.Equal(ConversationCompactor.PrunedStub(3000), ResultOf(stubbed[2], "r1"));
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3000), ArgumentOf(stubbed[3], "markdown"));
        Assert.Equal("T", ArgumentOf(stubbed[3], "title"));
        Assert.Null(stubbed[3].RawRepresentation);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3100), ArgumentOf(stubbed[6], "markdown"));
        Assert.Equal(ConversationCompactor.PrunedStub(3100), ResultOf(stubbed[9], "r2"));
        for (int i = 10; i < messages.Count; i++)
        {
            Assert.Same(messages[i], stubbed[i]);
        }

        Assert.Same(messages[4], stubbed[4]);
        Assert.Equal(Long(3000), ArgumentOf(messages[3], "markdown"));                // the held list untouched
        Assert.Equal(4, entries.Count);
        Assert.Equal(2, entries.Count(e => e.Argument == "markdown"));
        Assert.All(entries.Where(e => e.Argument is null), e => Assert.Equal("read_file", e.Tool));
    }

    [Fact]
    public void StubSupersededPlans_AbsoluteAndDotPaths_AndASharedToolMessage()
    {
        List<ChatMessage> messages =
        [
            User("plan it"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("r1", "read_file", new Dictionary<string, object?> { ["path"] = @"C:\work\.neon\plans\x.md" }),
                new FunctionCallContent("r2", "read_file", new Dictionary<string, object?> { ["path"] = "./.neon/plans/x.md" }),
                new FunctionCallContent("r3", "read_file", new Dictionary<string, object?> { ["path"] = "notes.md" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("r1", Long(900)), new FunctionResultContent("r2", Long(800)), new FunctionResultContent("r3", Long(700))]),
            Present("p1", Long(3000)), Result("p1", "saved"),
        ];

        var (stubbed, pruned, arguments) = ConversationCompactor.StubSupersededPlans(messages);

        Assert.Equal((2, 0), (pruned, arguments));
        Assert.Equal(ConversationCompactor.PrunedStub(900), ResultOf(stubbed[2], "r1"));
        Assert.Equal(ConversationCompactor.PrunedStub(800), ResultOf(stubbed[2], "r2"));
        Assert.Equal(Long(700), ResultOf(stubbed[2], "r3"));                            // not a plan read: kept
        Assert.Same(messages[3], stubbed[3]);
    }

    [Theory]
    [InlineData(".neon/plans/x.md", true)]
    [InlineData(@".neon\plans\x.md", true)]
    [InlineData("./.neon/plans/x.md", true)]
    [InlineData(@"C:\work\.NEON\Plans\x.md", true)]
    [InlineData("/Users/me/work/.neon/plans/x.md", true)]
    [InlineData("plans/x.md", false)]
    [InlineData("work/neon/plans/x.md", false)]
    [InlineData(".neon/plansx/x.md", false)]
    public void IsPlanPath_TakesRelativeAbsoluteAndDotPaths(string path, bool expected) =>
        Assert.Equal(expected, ConversationCompactor.IsPlanPath(path));

    /// <summary>One presentation: a plan read before it (as after <c>/plan open</c>) is stubbed; with none, nothing changes.</summary>
    [Fact]
    public void StubSupersededPlans_OnePresentation_StubsAReadBeforeIt_ElseNothing()
    {
        List<ChatMessage> read =
        [
            User("plan it"),
            ReadPlan("r1", ".neon/plans/x.md"), Result("r1", Long(3000)),
            Present("p1", Long(3000)), Result("p1", "saved"),
        ];
        var (stubbed, pruned, arguments) = ConversationCompactor.StubSupersededPlans(read);
        Assert.Equal((1, 0), (pruned, arguments));
        Assert.Equal(ConversationCompactor.PrunedStub(3000), ResultOf(stubbed[2], "r1"));
        Assert.Same(read[3], stubbed[3]);

        List<ChatMessage> bare = [User("plan it"), Present("p1", Long(3000)), Result("p1", "saved")];
        var (same, none, noArguments) = ConversationCompactor.StubSupersededPlans(bare);
        Assert.Equal((0, 0), (none, noArguments));
        Assert.Equal(bare, same);

        List<ChatMessage> noPlan = [User("hi"), ReadPlan("r1", ".neon/plans/x.md"), Result("r1", Long(3000))];
        var (untouched, zero, zeroArguments) = ConversationCompactor.StubSupersededPlans(noPlan);
        Assert.Equal((0, 0), (zero, zeroArguments));
        Assert.Equal(noPlan, untouched);
    }

    /// <summary>A non-plan read, a call that failed to parse and an opening call stay; a stubbed latest presentation does not count as the latest.</summary>
    [Fact]
    public void StubSupersededPlans_LeavesWhatItMustNotTouch()
    {
        var broken = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("pb", NeonSidekick.Llm.Tools.PresentPlanTool.ToolName) { Exception = new System.Text.Json.JsonException("cut") }]);
        var opening = ReadPlan(NeonSidekick.Llm.Assistant.OpeningCwdCallId, ".neon/plans/x.md");
        List<ChatMessage> messages =
        [
            opening, Result(NeonSidekick.Llm.Assistant.OpeningCwdCallId, Long(500)),
            User("plan it"),
            ReadPlan("r1", "src/app.cs"), Result("r1", Long(3000)),
            broken, Result("pb", "Error: could not be parsed"),
            Present("p1", Long(3000)), Result("p1", "saved"),
            Present("p2", Long(3100)), Result("p2", "saved"),
            Present("p3", ConversationCompactor.PrunedArgumentStub(3000)), Result("p3", "Error: markdown is a pruned stub"),
        ];

        var (stubbed, pruned, arguments) = ConversationCompactor.StubSupersededPlans(messages);

        Assert.Equal((0, 1), (pruned, arguments));
        Assert.Same(messages[0], stubbed[0]);
        Assert.Same(messages[1], stubbed[1]);
        Assert.Same(messages[4], stubbed[4]);
        Assert.Same(broken, stubbed[5]);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3000), ArgumentOf(stubbed[7], "markdown"));
        Assert.Same(messages[9], stubbed[9]);                                            // p2 is the latest real presentation
        Assert.Same(messages[11], stubbed[11]);
    }

    /// <summary>A refused presentation with real text (a review's checked missing) is the latest: kept, and the saved one before it stubbed.</summary>
    [Fact]
    public void StubSupersededPlans_ARefusedLatestPresentation_IsKept()
    {
        List<ChatMessage> messages =
        [
            User("close the gaps"),
            Present("p1", Long(3000)), Result("p1", "saved"),
            Present("p2", Long(3100)), Result("p2", NeonSidekick.Plans.PlanText.CheckedRequiredResult),
        ];

        var (stubbed, _, arguments) = ConversationCompactor.StubSupersededPlans(messages);

        Assert.Equal(1, arguments);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3000), ArgumentOf(stubbed[1], "markdown"));
        Assert.Same(messages[3], stubbed[3]);
    }

    /// <summary>Two turns, each with a presentation: the older turn's.</summary>
    private static List<ChatMessage> TwoPresentations() =>
    [
        User("plan it"), Present("p1", Long(3000)), Result("p1", "saved"), Assistant("presented"),
        User("revise"), Present("p2", Long(3100)), Result("p2", "saved"), Assistant("presented again"),
    ];

    [Fact]
    public async Task RunAsync_Summary_StubsSupersededPlansFirst_TheSummariserReadsTheStub()
    {
        var (client, assistant, tally) = Build(TwoPresentations());
        client.Enqueue(FakeChatClient.Text("A summary."), FakeChatClient.Usage(100, 5));

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Summary, keepRecent: 1, focus: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Summarised);
        Assert.Equal(1, result.ArgumentsPruned);
        Assert.Contains(result.Entries, e => e.Argument == "markdown" && e.Characters == 3000);
        var request = Assert.Single(client.Requests);
        Assert.Contains(request.SelectMany(m => m.Contents).OfType<FunctionCallContent>(), c => c.CallId == "p1" && c.Arguments!["markdown"]?.ToString() == ConversationCompactor.PrunedArgumentStub(3000));
        Assert.Equal(Long(3100), assistant.History.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single(c => c.CallId == "p2").Arguments!["markdown"]?.ToString());
    }

    [Fact]
    public async Task RunAsync_Prune_StubsSupersededPlans_AndTheyAloneAreAResult()
    {
        var (client, assistant, tally) = Build(TwoPresentations());

        var result = await ConversationCompactor.RunAsync(assistant, tally, CompactMode.Prune, keepRecent: 1, focus: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.ArgumentsPruned);
        Assert.Empty(client.Requests);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3000), assistant.History.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single(c => c.CallId == "p1").Arguments!["markdown"]?.ToString());

        // Nothing older to compact: the superseded copy alone is still a result, not "nothing to do".
        var (_, kept, keptTally) = Build(TwoPresentations());
        var alone = await ConversationCompactor.RunAsync(kept, keptTally, CompactMode.Summary, keepRecent: 5, focus: null, CancellationToken.None);
        Assert.NotNull(alone);
        Assert.False(alone.Summarised);
        Assert.Equal(1, alone.ArgumentsPruned);
        Assert.Equal(ConversationCompactor.PrunedArgumentStub(3000), kept.History.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single(c => c.CallId == "p1").Arguments!["markdown"]?.ToString());
    }
}
