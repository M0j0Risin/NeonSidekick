using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class TokenTallyTests
{
    private static TokenUsage One(long input, long output, double seconds = 1) =>
        new(input, output, input + output, 1, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Fresh_IsEmptyEverywhere()
    {
        var tally = new TokenTally();

        Assert.True(tally.LastReply.IsEmpty);
        Assert.True(tally.Conversation.IsEmpty);
        Assert.True(tally.Session.IsEmpty);
        Assert.Equal(0, tally.UnreportedReplies);
    }

    [Fact]
    public void Turns_SumIntoEveryScope_AndTheLastReplyStartsOver()
    {
        var tally = new TokenTally();

        tally.BeginTurn();
        tally.Add(One(100, 10));   // a tool-call request
        tally.Add(One(120, 30));   // the reply after it
        tally.EndTurn();

        Assert.Equal(One(100, 10) + One(120, 30), tally.LastReply);
        Assert.Equal(2, tally.LastReply.Requests);

        tally.BeginTurn();
        Assert.True(tally.LastReply.IsEmpty);          // the last reply's figures make way
        Assert.Equal(2, tally.Conversation.Requests);  // the conversation's stay
        tally.Add(One(150, 20));
        tally.EndTurn();

        Assert.Equal(One(150, 20), tally.LastReply);
        Assert.Equal(3, tally.Conversation.Requests);
        Assert.Equal(370, tally.Conversation.Input);
        Assert.Equal(tally.Conversation, tally.Session);
        Assert.Equal(0, tally.UnreportedReplies);
    }

    [Fact]
    public void AReasoningCount_ReachesEveryScope_AndTheDashOnlyBeforeOne()
    {
        var tally = new TokenTally();

        tally.BeginTurn();
        tally.Add(One(100, 10));   // a server that counts no thinking
        tally.EndTurn();
        Assert.Null(tally.LastReply.Reasoning);
        Assert.Null(tally.Conversation.Reasoning);

        tally.BeginTurn();
        tally.Add(One(120, 30) with { Reasoning = 25 });
        tally.EndTurn();

        Assert.Equal(25, tally.LastRequest.Reasoning);
        Assert.Equal(25, tally.LastReply.Reasoning);
        Assert.Equal(25, tally.Conversation.Reasoning);   // the one report, not a dash for the mixed sum
        Assert.Equal(25, tally.Session.Reasoning);

        tally.ResetConversation();
        Assert.Null(tally.Conversation.Reasoning);
        Assert.Equal(25, tally.Session.Reasoning);
    }

    [Fact]
    public void ATurnWithNoReport_CountsAsUnreported_OnlyWhenEnded()
    {
        var tally = new TokenTally();

        tally.BeginTurn();   // a cancelled turn: never ended
        tally.BeginTurn();
        tally.EndTurn();     // a completed reply the server said nothing about
        tally.BeginTurn();
        tally.EndTurn();

        Assert.Equal(2, tally.UnreportedReplies);
        Assert.True(tally.Session.IsEmpty);
    }

    [Fact]
    public void ResetConversation_KeepsTheSession_AndTheLastReply()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(One(100, 10));
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();

        tally.ResetConversation();

        Assert.True(tally.Conversation.IsEmpty);
        Assert.Equal(0, tally.UnreportedReplies);
        Assert.Equal(One(100, 10), tally.Session);
        Assert.True(tally.LastReply.IsEmpty);   // the unreported one, as it was

        tally.BeginTurn();
        tally.Add(One(50, 5));
        tally.EndTurn();

        Assert.Equal(One(50, 5), tally.Conversation);
        Assert.Equal(One(100, 10) + One(50, 5), tally.Session);
    }

    [Fact]
    public void LastRequest_IsTheLatestAdd_NotASum_AndGoesWithTheConversation()
    {
        var tally = new TokenTally();
        Assert.True(tally.LastRequest.IsEmpty);

        // A tool-calling turn: two requests; the last request is the second alone, the reply their sum.
        tally.BeginTurn();
        tally.Add(One(100, 10));
        tally.Add(One(130, 20));
        tally.EndTurn();
        Assert.Equal(One(130, 20), tally.LastRequest);
        Assert.Equal(One(100, 10) + One(130, 20), tally.LastReply);

        // The next turn starts: the last request stands until it reports (the hint row keeps its share).
        tally.BeginTurn();
        Assert.Equal(One(130, 20), tally.LastRequest);
        tally.EndTurn();
        Assert.Equal(One(130, 20), tally.LastRequest);

        // The history was cleared: nothing is in the context any more.
        tally.ResetConversation();
        Assert.True(tally.LastRequest.IsEmpty);
    }

    [Fact]
    public void AddCompaction_BillsTheConversationAndTheSession_ZeroesTheContext_KeepsTheLastReply()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(One(4000, 100));
        tally.EndTurn();

        tally.AddCompaction(One(3900, 50, seconds: 2));

        Assert.Equal(One(4000, 100) + One(3900, 50, seconds: 2), tally.Conversation);
        Assert.Equal(2, tally.Session.Requests);
        Assert.Equal(One(4000, 100), tally.LastReply);   // the reply shown is still the last reply
        Assert.True(tally.LastRequest.IsEmpty);          // measured again at the next reply
        Assert.Equal(0, tally.UnreportedReplies);

        // A prune, or a server that reported nothing: nothing billed, the context still zeroed.
        tally.Add(One(500, 20));
        tally.AddCompaction(null);
        Assert.Equal(3, tally.Conversation.Requests);
        Assert.True(tally.LastRequest.IsEmpty);
    }

    [Fact]
    public void AddLearning_BillsTheConversationAndTheSession_CountsItsOwn_LeavesTheContextAndTheLastReply()
    {
        var tally = new TokenTally();
        tally.BeginTurn();
        tally.Add(One(4000, 100));
        tally.EndTurn();
        Assert.Equal(0, tally.LearningRequests);
        Assert.True(tally.Learning.IsEmpty);

        tally.AddLearning(One(3900, 50, seconds: 2) + One(200, 300), requests: 2);

        Assert.Equal(One(4000, 100) + One(3900, 50, seconds: 2) + One(200, 300), tally.Conversation);
        Assert.Equal(tally.Conversation, tally.Session);
        Assert.Equal(One(4000, 100), tally.LastReply);      // the reply shown is still the last reply
        Assert.Equal(One(4000, 100), tally.LastRequest);    // the context in use is the turn's, never the reflection's
        Assert.Equal(2, tally.LearningRequests);
        Assert.Equal(4450, tally.Learning.Total);
        Assert.Equal(0, tally.UnreportedReplies);

        // A reflection the server reported nothing for still counts its request; /clear keeps the session's figure.
        tally.AddLearning(TokenUsage.Zero, requests: 1);
        tally.ResetConversation();
        Assert.True(tally.Conversation.IsEmpty);
        Assert.Equal(3, tally.LearningRequests);
        Assert.Equal(4450, tally.Learning.Total);
    }

    [Fact]
    public void LastKnownSpeed_IsThisTurnsOnceItReported_ElseTheTurnBefore()
    {
        var tally = new TokenTally();
        Assert.Null(tally.LastKnownSpeed);

        tally.BeginTurn();
        tally.Add(One(100, 40, seconds: 2));   // 20 tok/s
        tally.EndTurn();
        Assert.Equal(20, tally.LastKnownSpeed);

        // A new turn zeroes the last reply, not the speed the busy row shows.
        tally.BeginTurn();
        Assert.True(tally.LastReply.IsEmpty);
        Assert.Equal(20, tally.LastKnownSpeed);

        tally.Add(One(200, 30, seconds: 1));   // 30 tok/s
        Assert.Equal(30, tally.LastKnownSpeed);

        // A turn with nothing reported keeps the one before it.
        tally.EndTurn();
        tally.BeginTurn();
        tally.EndTurn();
        tally.BeginTurn();
        Assert.Equal(30, tally.LastKnownSpeed);
    }
}
