using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>/rewind</c>'s core and words (2026-09-30).</summary>
public class ConversationRewindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void Call(ConversationHistory history, string callId, string name)
    {
        history.AddMessage(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, new Dictionary<string, object?>())]));
        history.AddToolResults([new FunctionResultContent(callId, "ok")]);
    }

    /// <summary>Three turns: the first opened by the seeded clock call, the second writes a file and runs a command, the third reads.</summary>
    private static ConversationHistory ThreeTurns()
    {
        var history = new ConversationHistory("s");
        history.AddUser("one");
        Call(history, Assistant.OpeningClockCallId, "get_current_time");
        history.AddAssistant("r1");
        history.AddUser("two");
        Call(history, "c1", "write_file");
        Call(history, "c2", "run_command");
        Call(history, "c3", "write_file");
        history.AddAssistant("r2");
        history.AddUser("three");
        Call(history, "c4", "read_file");
        history.AddAssistant("r3");
        return history;
    }

    [Fact]
    public void Turns_ListsEveryTurn_TheSummaryLeftOut_TheOpeningCallsUncounted()
    {
        var history = ThreeTurns();
        var turns = ConversationRewind.Turns(history.Messages);

        Assert.Equal(["one", "two", "three"], turns.Select(t => t.Text));
        Assert.Equal([1, 2, 3], turns.Select(t => t.Number));
        Assert.Equal([0, 3, 1], turns.Select(t => t.ToolCalls));
        Assert.Equal(history.TurnStarts(), turns.Select(t => t.Start));

        // After a compact: the summary turn is no place to go back to, and the numbering starts after it.
        var compacted = new ConversationHistory("s");
        compacted.AddUser(ConversationCompactor.SummaryPreamble + "they talked");
        compacted.AddUser("four");
        compacted.AddAssistant("r4");
        var after = ConversationRewind.Turns(compacted.Messages);
        Assert.Equal("four", Assert.Single(after).Text);
        Assert.Equal(1, after[0].Number);
        Assert.Empty(ConversationRewind.Turns([]));
    }

    [Fact]
    public void UserLine_LeavesTheProgressNoteOut_AndTurnsAClaudeTagBackIntoTheCommand()
    {
        var progressed = new ChatMessage(ChatRole.User,
        [
            new TextContent("do the thing"),
            new TextContent(ConversationCompactor.TurnProgressPreamble + "half done") { AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationCompactor.TurnProgressKey] = true } },
        ]);
        var restored = new ChatMessage(ChatRole.User, [new TextContent("do the thing"), new TextContent(ConversationCompactor.TurnProgressPreamble + "half done")]);

        Assert.Equal("do the thing", ConversationRewind.UserLine(progressed));
        Assert.Equal("do the thing", ConversationRewind.UserLine(restored));
        Assert.Equal("/claude why?", ConversationRewind.UserLine(new ChatMessage(ChatRole.User, ClaudeText.HistoryUser("why?"))));
        Assert.Equal("look [Image #1]", ConversationRewind.UserLine(new ChatMessage(ChatRole.User, [new TextContent("look [Image #1]"), new DataContent(new byte[] { 1 }, "image/png")])));
    }

    [Fact]
    public void Preview_NamesTheChangingTools_TheClaudeThreads_AndTheFirstOrdinal()
    {
        var history = ThreeTurns();
        ConversationHistory.SetTurnOrdinal(history.Messages[history.TurnStarts()[1]], 5);
        ConversationHistory.SetTurnOrdinal(history.Messages[history.TurnStarts()[2]], 6);

        var cut = ConversationRewind.Preview(history.Messages, history.TurnStarts()[1]);

        Assert.Equal(2, cut.Turns);
        Assert.Equal(["write_file", "run_command"], cut.ChangingTools);   // read_file only reads; each name once, in call order
        Assert.False(cut.HadClaude);
        Assert.False(cut.HadAdvisor);
        Assert.Equal(5, cut.FirstOrdinal);
        Assert.False(cut.KeptStamped);
        Assert.Equal(3, history.TurnCount);   // nothing changed

        var claude = new ConversationHistory("s");
        claude.AddUser("hi");
        ConversationHistory.SetTurnOrdinal(claude.Messages[0], 1);
        claude.AddUser(ClaudeText.HistoryUser("why?"));
        claude.AddAssistant(ClaudeText.HistoryReply("because"));
        claude.AddUser("ask the advisor");
        Call(claude, "a1", Llm.Tools.ClaudeAdvisorTool.ToolName);
        var both = ConversationRewind.Preview(claude.Messages, 1);
        Assert.True(both.HadClaude);
        Assert.True(both.HadAdvisor);
        Assert.Empty(both.ChangingTools);   // the advisor only reads
        Assert.Null(both.FirstOrdinal);
        Assert.True(both.KeptStamped);
    }

    [Theory]
    [InlineData(5, false, 3, 10, 4)]      // a stamp in the cut: the rows before it stay
    [InlineData(null, true, 3, 10, 10)]   // none in the cut, one kept: the cut turns wrote no rows
    [InlineData(null, false, 3, 10, 7)]   // no stamps at all: one row per cut turn, from the end
    [InlineData(null, false, 3, 2, 0)]    // never below nothing
    [InlineData(12, false, 1, 10, 10)]    // never past what is stored
    public void KeepThrough_GoesByTheStamp_ElseByWhatWasLogged(int? first, bool keptStamped, int turns, int stored, int expected)
    {
        Assert.Equal(expected, ConversationRewind.KeepThrough(new RewindCut(turns, [], false, false, first, keptStamped), stored));
    }

    [Fact]
    public void Apply_CutsTheHistory_AndTheSessionRowsByTheStamp()
    {
        using var store = new SessionStore(_dir, _time);
        long id = store.Begin("one", "m")!.Value;
        var history = new ConversationHistory("s");
        foreach (string line in new[] { "one", "two", "three" })
        {
            history.AddUser(line);
            history.AddAssistant("re " + line);
            ConversationHistory.SetTurnOrdinal(history.LastTurnStart()!, store.AppendTurn(id, line, "re " + line, 0, [], [], 0, 1, 1, false)!.Value);
        }

        var turns = ConversationRewind.Turns(history.Messages);
        var (cut, picked) = ConversationRewind.Apply(history, turns[1], store, id);

        Assert.Equal(2, cut.Turns);
        Assert.Equal("two", picked.Text);
        Assert.Equal(["one", "re one"], history.Messages.Select(m => m.Text));
        Assert.Equal(["one"], store.Load(id)!.Turns.Select(t => t.UserText));
        Assert.Throws<ArgumentException>(() => ConversationRewind.Apply(history, turns[2], null, null));   // no such turn any more
    }

    [Fact]
    public void Replay_GivesEachTurnsLineReplyAndCalls_TheSummaryMarked()
    {
        var history = new ConversationHistory("s");
        history.AddUser(ConversationCompactor.SummaryPreamble + "earlier");
        history.AddUser("two");
        Call(history, "c1", "read_file");
        history.AddAssistant("r2");
        history.AddUser(ClaudeText.HistoryUser("why?"));
        history.AddAssistant(ClaudeText.HistoryReply("because"));

        var replay = ConversationRewind.Replay(history.Messages);

        Assert.Equal(3, replay.Count);
        Assert.True(replay[0].Summary);
        Assert.Equal(new RewindReplay("two", "r2", 1, false), replay[1]);
        Assert.Equal(new RewindReplay("/claude why?", "because", 0, false), replay[2]);
    }

    [Fact]
    public void ImageLabels_FindsEachPictureLabel_InOrder()
    {
        Assert.Equal([(5, 10), (20, 11)], ConversationRewind.ImageLabels("look [Image #1] and [Image #12]"));
        Assert.Empty(ConversationRewind.ImageLabels("[Image #] [Image #x] [Image #3"));
        Assert.Single(ConversationRewind.Images(new ChatMessage(ChatRole.User, [new TextContent("a"), new DataContent(new byte[] { 1 }, ImageFile.Png)])));
    }

    [Fact]
    public void ParseCount_ReadsAWholePositiveNumber_EmptyIsOne()
    {
        Assert.Equal(1, RewindText.ParseCount(""));
        Assert.Equal(1, RewindText.ParseCount("  "));
        Assert.Equal(3, RewindText.ParseCount(" 3 "));
        Assert.Null(RewindText.ParseCount("0"));
        Assert.Null(RewindText.ParseCount("-1"));
        Assert.Null(RewindText.ParseCount("two"));
        Assert.Null(RewindText.ParseCount("1.5"));
    }

    [Fact]
    public void Words_ArePinned()
    {
        var turn = new RewindTurn(3, 9, "fix the build\nand the tests", 2);
        var cut = new RewindCut(2, ["write_file", "run_command"], false, false, null, false);

        Assert.Equal("↩️ Rewind", RewindText.Title);
        Assert.Equal("↩️ Rewind to before #3? 2 messages go.", RewindText.ConfirmPrompt(turn, cut));
        Assert.Equal("↩️ Rewind to before #3? 1 message goes.", RewindText.ConfirmPrompt(turn, cut with { Turns = 1 }));
        Assert.Equal("Not undone: what write_file, run_command changed stays as it is.", RewindText.ConfirmCaption(cut));
        Assert.Null(RewindText.ConfirmCaption(cut with { ChangingTools = [] }));
        Assert.Equal("(↩️ rewound 2 messages: \"fix the build …\")", RewindText.RewoundNotice(2, turn.Text));
        Assert.Equal("What write_file, run_command changed stays as it is: rewinding does not undo it.", RewindText.ChangesStayWarning(cut.ChangingTools));
        Assert.Equal("(↩️ nothing to rewind)", RewindText.NothingNotice);
        Assert.Equal("/rewind takes a number of messages back, from 1 to 4.", RewindText.UsageError(4));
        Assert.Equal("/rewind takes a number of messages back: 1 is the only one.", RewindText.UsageError(1));
        Assert.Equal("Rewound 2 turns (to before turn 3)", RewindText.RewoundLogLine(2, 3));
        Assert.Equal("the message was: hi", RewindText.HeadlessLineNotice("hi"));
        Assert.Equal("Session 4: rewound to turn 1 (2 turns removed)", SessionStore.TruncatedLogLine(4, 1, 2));
        Assert.Equal(UI.Theme.DimMarkup("#3") + "  fix the build …  " + UI.Theme.DimMarkup("🛠️ 2"), RewindText.RowMarkup(turn));
    }
}
