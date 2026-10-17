using System.Net;
using System.Text.Json;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's pure parts (2026-10-14): the transcript feed, the asks' broker, the session's messages and notices, the reach word, the
/// sign-in's token and cookie, the reply's HTML, the shopping list's page, and the controller over a fake server.
/// </summary>
public sealed partial class WebUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStoreHolder _holder;
    private readonly ShoppingList _list;
    private readonly WebUiSession _session;
    private int _nudges;
    private int _shoppingChanges;

    public WebUiTests()
    {
        _holder = new ShoppingStoreHolder(() => _dir, _time);
        _list = new ShoppingList(_holder.Current);
        _session = new WebUiSession(State, _ => new WebReply(WebOutcome.Conflict, WebUiText.NothingToStop), () => _list, () => _shoppingChanges++, _time)
        {
            Nudge = () => _nudges++,
        };
    }

    private WebState State() => new("p", "🦊", "🙂", "m", false, false, false, "", _session?.Waiting ?? [], null, "1");

    public void Dispose()
    {
        _session.Dispose();
        _holder.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private List<FeedChange> Watch(TranscriptFeed feed)
    {
        var changes = new List<FeedChange>();
        feed.Changed += changes.Add;
        return changes;
    }

    // ── The feed ─────────────────────────────────────────────────────────────

    [Fact]
    public void AReply_IsOneEntry_ThatGrows_AndEnds()
    {
        var feed = new TranscriptFeed();
        var changes = Watch(feed);

        feed.Begin("🦊");
        feed.Append("Hello");
        feed.Append(" **there**");
        feed.End();

        var entry = Assert.Single(feed.Snapshot(m => "<p>" + m + "</p>"));
        long seq = entry.Seq;
        Assert.Equal((seq, "assistant", "Hello **there**", "🦊", "<p>Hello **there**</p>"), (entry.Seq, entry.Kind, entry.Text, entry.Glyph, entry.Html));
        Assert.Collection(changes,
            c => Assert.IsType<FeedChange.Appended>(c),
            c => Assert.Equal(new FeedChange.Grew(seq, 0, "Hello"), c),
            c => Assert.Equal(new FeedChange.Grew(seq, 5, " **there**"), c),
            c => Assert.Equal(new FeedChange.Ended(seq, "Hello **there**"), c));
    }

    [Fact]
    public void AToolLine_EndsTheReplysStretch_AndTheTextAfterIt_Continues()
    {
        var feed = new TranscriptFeed();
        feed.Begin("🦊");
        feed.Append("Looking.");
        feed.Add(FeedKind.Tool, "🛠️ web_search cats");
        feed.Append("Found them.");
        feed.End();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["assistant", "tool", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal("🦊", entries[2].Glyph);
        Assert.Null(entries[0].Continues);
        Assert.True(entries[2].Continues);   // the reply had spoken: the page draws it flush, as the PC does
        Assert.All(entries.Where(e => e.Kind == "assistant"), e => Assert.NotNull(e.Html));
    }

    // ── What the terminal shows besides (2026-10-15) ────────────────────────

    [Fact]
    public void ALazyReply_PushesNothingUntilItsWords_AndAToolsOnlyReplyLeavesNoBubble()
    {
        var feed = new TranscriptFeed();
        feed.Begin("🦊");
        feed.Append("\n\n");   // blanks before the words are nobody's
        Assert.Equal(0, feed.Count);

        feed.Add(FeedKind.Tool, "🛠️ read_file a.txt");
        feed.Append("  Done.");
        feed.End();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["tool", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal("Done.", entries[1].Text);
        Assert.Null(entries[1].Continues);   // nothing said before the tool: the glyph goes with the words
        Assert.Null(entries[1].NoReply);

        var tools = new TranscriptFeed();
        tools.Begin("🦊");
        tools.Add(FeedKind.Tool, "🛠️ read_file a.txt");
        tools.End();
        Assert.Equal(["tool"], tools.Snapshot(m => m).Select(e => e.Kind));
    }

    [Fact]
    public void AReplyThatSaidNothing_EndsAsNoReply_WithItsGlyph()
    {
        var feed = new TranscriptFeed();
        var changes = Watch(feed);
        feed.Begin("🦊");
        feed.End();

        var entry = Assert.Single(feed.Snapshot(m => "<p>" + m + "</p>"));
        Assert.Equal(("assistant", "", "🦊", (bool?)true, (string?)null), (entry.Kind, entry.Text, entry.Glyph, entry.NoReply, entry.Html));
        Assert.IsType<FeedChange.Appended>(Assert.Single(changes));
    }

    [Fact]
    public void Thinking_StreamsIntoItsOwnEntry_AndFoldsUnderItsSummaryWhenTheWordsCome()
    {
        var feed = new TranscriptFeed();
        var changes = Watch(feed);
        feed.Begin("🦊");
        feed.AppendThinking("  \n", TimeSpan.Zero);   // nothing yet
        feed.AppendThinking("The user wants the time. ", TimeSpan.FromSeconds(1));
        feed.AppendThinking("I'll check.", TimeSpan.FromSeconds(4.2));
        feed.Append("It is noon.");
        feed.End();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["thinking", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal("The user wants the time. I'll check.", entries[0].Text);
        Assert.Equal(ThinkingFoldText.Bare(TimeSpan.FromSeconds(4.2), entries[0].Text), entries[0].Summary);
        Assert.True(entries[0].Ended);
        Assert.Null(entries[1].Continues);   // the thinking is no speech: the glyph comes with the words
        var ended = changes.OfType<FeedChange.Ended>().First();
        Assert.Equal((FeedKind.Thinking, entries[0].Summary), (ended.Kind, ended.Summary));
    }

    [Fact]
    public void Thinking_IsCappedOnce_ThenKeepsNothingMore()
    {
        var feed = new TranscriptFeed();
        feed.AppendThinking(new string('a', TranscriptFeed.MaxThinkingChars - 10), TimeSpan.Zero);
        feed.AppendThinking(new string('b', 50), TimeSpan.Zero);
        feed.AppendThinking("more", TimeSpan.Zero);

        string text = Assert.Single(feed.Snapshot(m => m)).Text;
        Assert.Equal(TranscriptFeed.MaxThinkingChars + 1, text.Length);
        Assert.EndsWith("bbb…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_GroupsAStreamsLines_AndASwitchStartsAnother()
    {
        var feed = new TranscriptFeed();
        feed.AppendOutput("one", isError: false);
        feed.AppendOutput("two", isError: false);
        feed.AppendOutput("oops", isError: true);
        feed.AppendOutput("three", isError: false);
        feed.Add(FeedKind.Notice, "(exit 0)");

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["output", "output", "output", "notice"], entries.Select(e => e.Kind));
        Assert.Equal(("one\ntwo", false), (entries[0].Text, entries[0].Failed));
        Assert.True(entries[1].Failed);
        Assert.True(entries[2].Ended);
    }

    [Fact]
    public void ToolLines_StayFlat_WithNoKeep()
    {
        var feed = new TranscriptFeed();
        feed.ToolSummary("🛠️ 1 tool call — grep", failed: false);
        feed.Add(FeedKind.Tool, "🛠️ grep x");
        Assert.Equal(["tool"], feed.Snapshot(m => m).Select(e => e.Kind));
        Assert.Null(feed.Snapshot(m => m)[0].Run);
    }

    [Fact]
    public void ARun_GathersItsToolLines_UnderTheHeldSummary_RewrittenAsCallsAreCounted()
    {
        var feed = new TranscriptFeed { ToolKeep = () => 2 };
        var changes = Watch(feed);

        feed.ToolSummary("🛠️ 1 tool call — grep", failed: false);   // counted before its line is written
        feed.Add(FeedKind.Tool, "🛠️ grep x");
        feed.Add(FeedKind.ToolResult, "3 matches", joins: true);
        feed.ToolSummary("🛠️ 2 tool calls · 1 failed — grep ×2", failed: true);
        feed.Add(FeedKind.Tool, "🛠️ grep y");
        feed.Add(FeedKind.ToolResult, "Error: no file", failed: true, joins: true);
        feed.Add(FeedKind.Notice, "(a notice ends it)");

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["run", "tool", "result", "tool", "result", "notice"], entries.Select(e => e.Kind));
        var run = entries[0];
        Assert.Equal(("🛠️ 2 tool calls · 1 failed — grep ×2", true, (int?)2, (bool?)true), (run.Text, run.Failed, run.Keep, run.Ended));
        Assert.All(entries.Skip(1).Take(4), e => Assert.Equal(run.Seq, e.Run));
        Assert.Equal([null, true, null, true], entries.Skip(1).Take(4).Select(e => e.Joins));
        Assert.Null(entries[5].Run);
        Assert.Contains(changes, c => c is FeedChange.Replaced r && r.Seq == run.Seq && r.Failed);
        Assert.Contains(changes, c => c is FeedChange.Ended { Kind: FeedKind.Run } e && e.Seq == run.Seq);
    }

    [Fact]
    public void EndToolRun_EndsTheRun_AndTheNextToolLineOpensAnother()
    {
        var feed = new TranscriptFeed { ToolKeep = () => 1 };
        feed.Add(FeedKind.Tool, "🛠️ a");
        feed.EndToolRun();
        feed.Add(FeedKind.Tool, "🛠️ b");
        feed.Append("words end it too");

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["run", "tool", "run", "tool", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal("", entries[2].Text);   // the tally began again
        Assert.True(entries[2].Ended);
    }

    [Fact]
    public void ARunsChildren_OutliveATrimmedHead_OnTheirOwn()
    {
        var feed = new TranscriptFeed { ToolKeep = () => 1 };
        feed.Add(FeedKind.Tool, "🛠️ first");
        feed.Add(FeedKind.Notice, "end");
        for (int i = 0; i < TranscriptFeed.MaxEntries - 2; i++)
        {
            feed.Add(FeedKind.Notice, "n" + i);
        }

        var entries = feed.Snapshot(m => m);
        Assert.Equal("tool", entries[0].Kind);   // its run's head went first
        Assert.NotNull(entries[0].Run);
    }

    [Fact]
    public void TheSession_SendsAThinkingEndsSummary_WithNoHtml_AndARunsReplace()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Feed.ToolKeep = () => 1;
        _session.Feed.AppendThinking("Hmm.", TimeSpan.FromSeconds(2));
        _session.Feed.ToolSummary("🛠️ 1 tool call — grep", failed: false);
        _session.Feed.Add(FeedKind.Tool, "🛠️ grep x");
        _session.Feed.ToolSummary("🛠️ 1 tool call · 1 failed — grep", failed: true);

        var events = Drain(subscription);
        var end = JsonSerializer.Deserialize(events.First(e => e.Name == "end").Json.Value, WebUiJsonContext.Default.WebEnd)!;
        Assert.Null(end.Html);
        Assert.Equal(ThinkingFoldText.Bare(TimeSpan.FromSeconds(2), "Hmm."), end.Summary);
        var replace = JsonSerializer.Deserialize(events.Single(e => e.Name == "replace").Json.Value, WebUiJsonContext.Default.WebReplaced)!;
        Assert.True(replace.Failed);
        var run = _session.Snapshot().Entries.Single(e => e.Kind == "run");
        Assert.Equal("🛠️ 1 tool call · 1 failed — grep", run.Text);
    }

    [Fact]
    public void AStreamingReply_HasNoHtmlYet_AndAToolResultIsCut()
    {
        var feed = new TranscriptFeed();
        feed.Add(FeedKind.ToolResult, new string('x', 1000), failed: true);
        feed.Begin(null);
        feed.Append("half");

        var entries = feed.Snapshot(m => "html");
        Assert.Equal(TranscriptFeed.MaxResultChars + 1, entries[0].Text.Length);
        Assert.True(entries[0].Failed);
        Assert.Null(entries[1].Html);
    }

    [Fact]
    public void TheFeed_KeepsTheNewestEntries_AndClearSaysSo()
    {
        var feed = new TranscriptFeed();
        for (int i = 0; i < TranscriptFeed.MaxEntries + 50; i++)
        {
            feed.Add(FeedKind.Notice, "n" + i);
        }

        Assert.Equal(TranscriptFeed.MaxEntries, feed.Count);
        Assert.Equal("n50", feed.Snapshot(m => m)[0].Text);

        var changes = Watch(feed);
        feed.Clear();
        Assert.Equal(0, feed.Count);
        Assert.IsType<FeedChange.Cleared>(Assert.Single(changes));
    }

    [Fact]
    public void TheRenderer_RecordsWhatItDraws()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var feed = new TranscriptFeed();
        var renderer = new TranscriptRenderer(console) { Feed = feed };

        renderer.User("hi");
        renderer.Tool("web_search", "{\"query\":\"cats\"}");
        renderer.ToolResult("web_search", "3 results");
        renderer.Notice("(a notice)");
        renderer.Rule();
        renderer.BeginAssistant(glyph: "🦊");
        renderer.AppendDelta("  Hello");
        renderer.EndAssistant();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["user", "tool", "result", "notice", "separator", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal(TranscriptRenderer.ToolText("web_search", "{\"query\":\"cats\"}"), entries[1].Text);
        Assert.Equal("Hello", entries[5].Text);   // the leading whitespace dropped, as on the screen
    }

    [Fact]
    public void TheRenderer_FeedsThinking_TheRunsSummary_AResultJoiningItsCall_AndCmdOutput()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var feed = new TranscriptFeed { ToolKeep = () => 3 };
        var renderer = new TranscriptRenderer(console) { Feed = feed };

        renderer.BeginAssistant(glyph: "🦊");
        renderer.AppendThinking("Look it up.", TimeSpan.FromSeconds(1));   // no pane: the page gets it all the same
        renderer.CountToolCall("web_search");
        renderer.Tool("web_search", "{\"query\":\"cats\"}");
        renderer.ToolResult("web_search", "3 results");
        renderer.AppendDelta("Found them.");
        renderer.EndAssistant();
        renderer.ShellOutput("line one", isError: false);
        renderer.ShellOutput("bad", isError: true);

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["thinking", "run", "tool", "result", "assistant", "output", "output"], entries.Select(e => e.Kind));
        Assert.Equal(ToolGroupText.Bare([("web_search", 1)]), entries[1].Text);
        Assert.True(entries[1].Ended);   // the words ended the run
        Assert.True(entries[3].Joins);
        Assert.True(entries[6].Failed);
    }

    [Fact]
    public void TheRenderer_FeedsAnEditsRows_CutAndFoldedAsOnThePc()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var feed = new TranscriptFeed();
        var renderer = new TranscriptRenderer(console) { Feed = feed };
        var diff = new NeonSidekick.Files.FileDiff("a.txt", false, [new NeonSidekick.Git.UnifiedDiff.Hunk(1, 3, 1, 3, [" one", "-two", "+TWO", " three"])], 1, 1);

        renderer.ToolDiff("edited a.txt", diff, maxLines: 3, collapseCount: 2);

        var web = Assert.Single(feed.Snapshot(m => m)).Diff!;
        Assert.Equal(NeonSidekick.Files.FileText.DiffSummary(diff), web.Head);
        Assert.Equal([(" ", 1, "one"), ("-", 2, "two"), ("+", 2, "TWO")], web.Rows.Select(r => (r!.Sign, r.Number, r.Text)));
        Assert.Equal(NeonSidekick.Files.FileText.DiffMore(1), web.More);
        Assert.Equal((2, DiffFoldText.Bare(diff, 4)), (web.Fold, web.FoldSummary));

        var whole = WebDiffs.From(diff, maxLines: 10, collapseCount: 0);
        Assert.Equal((4, (string?)null, 0, ""), (whole.Rows.Count, whole.More, whole.Fold, whole.FoldSummary));
        Assert.Empty(WebDiffs.From(diff, maxLines: 0, collapseCount: 2).Rows);
        Assert.Null(WebDiffs.From(diff, maxLines: 0, collapseCount: 2).More);
    }

    [Fact]
    public void TheBareSummaries_AreTheTerminalsWithoutTheirLead()
    {
        (string, int)[] tally = [("grep", 2), ("read_file", 1)];
        Assert.Equal(ToolGroupText.Summary(tally, expanded: false, failed: 1)[4..], ToolGroupText.Bare(tally, 1));
        Assert.StartsWith(TranscriptRenderer.ToolGlyph.TrimStart(), ToolGroupText.Bare(tally), StringComparison.Ordinal);
        Assert.EndsWith(ThinkingFoldText.Bare(TimeSpan.FromSeconds(3), "Hi. More."), ThinkingFoldText.Summary(TimeSpan.FromSeconds(3), expanded: false, "Hi. More."), StringComparison.Ordinal);
        Assert.StartsWith(ThinkingFoldText.Glyph, ThinkingFoldText.Bare(TimeSpan.FromSeconds(3), null), StringComparison.Ordinal);
        var diff = new NeonSidekick.Files.FileDiff("a.txt", false, [], 3, 1);
        Assert.EndsWith(DiffFoldText.Bare(diff, 14), DiffFoldText.Summary(diff, 14, expanded: true), StringComparison.Ordinal);
    }

    // ── The asks ─────────────────────────────────────────────────────────────

    private static WebAskSpec YesNo() => new("confirm", "Go?", [new WebChoice("no", "No"), new WebChoice("yes", "Yes")]);

    [Fact]
    public async Task AWebAnswer_ClaimsTheAsk_AndClosesIt()
    {
        var broker = new WebAskBroker();
        var handle = broker.Open(YesNo());

        var reply = broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "yes" }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Equal(AskSource.Web, handle.Winner);
        Assert.True(handle.Answered.IsCompletedSuccessfully);
        Assert.Equal("Yes", handle.Describe(await handle.Answered));
        Assert.Empty(broker.OpenAsks());
        Assert.False(handle.TryClaim(AskSource.Pc));   // the pane's answer, coming after, does not count
    }

    [Fact]
    public void ThePcClaimingFirst_LeavesTheWebAConflict()
    {
        var broker = new WebAskBroker();
        var handle = broker.Open(YesNo());
        Assert.True(handle.TryClaim(AskSource.Pc));

        var reply = broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "yes" }, null);

        Assert.Equal(WebOutcome.Conflict, reply.Outcome);
        Assert.False(handle.Answered.IsCompleted);
        Assert.Equal(WebOutcome.Conflict, broker.Answer(new WebAnswerRequest { AskId = "nope", Choice = "yes" }, null).Outcome);
    }

    [Fact]
    public void AnAnswer_MustFitTheAsk()
    {
        var questions = new WebAskSpec("question", "Questions", [], Questions: [new WebQuestion("Color?", null, false, ["red", "blue"]), new WebQuestion("Toppings?", null, true, ["cheese", "ham"])]);

        Assert.Equal(WebOutcome.Refused, WebAskBroker.Check(YesNo(), new WebAnswerRequest { Choice = "maybe" })!.Value.Outcome);
        Assert.Null(WebAskBroker.Check(YesNo(), new WebAnswerRequest { Choice = "no" }));
        // Every question answered, by a pick or typed words; one pick for a single choice.
        Assert.Equal(WebUiText.AskIncomplete, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }] })!.Value.Message);
        Assert.Equal(WebUiText.AskIncomplete, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }, new() { Picked = [] }] })!.Value.Message);
        Assert.Equal(WebUiText.NoSuchChoice, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [0, 1] }, new() { Picked = [0] }] })!.Value.Message);
        Assert.Equal(WebUiText.NoSuchChoice, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [5] }, new() { Picked = [0] }] })!.Value.Message);
        Assert.Null(WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Other = "green" }, new() { Picked = [0, 1], Other = "olives" }] }));
    }

    [Fact]
    public void AskUsersAnswers_ReadAsTheToolReadsThePane()
    {
        var questions = new List<Llm.Tools.AskQuestion> { new("Color?", "Color", false, ["red", "blue"]), new("Toppings?", null, true, ["cheese", "olives", "ham"]) };

        var answers = App.ChatScreen.QuestionAnswers(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }, new() { Picked = [2, 0], Other = " anchovies " }] });

        Assert.Equal(["blue"], answers[0].Choices);
        Assert.Equal(["cheese", "ham", "anchovies"], answers[1].Choices);   // in option order, the typed answer last
    }

    [Fact]
    public void ThePlansRefine_ReadsAsThePcsTypedLine()
    {
        var verdict = App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "refine", Text = "  add\r\ntests  " });
        Assert.Equal(new Plans.PlanVerdict(Plans.PlanChoice.Refine, "add tests"), verdict);
        Assert.Equal(new Plans.PlanVerdict(Plans.PlanChoice.Refine), App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "refine", Text = " " }));
        Assert.Equal(Plans.PlanChoice.ApproveFresh, App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "fresh" }).Choice);
    }

    [Fact]
    public void AnApproval_OnThePage_IsTheWholeCommand_InThePcsWords()
    {
        var request = new Shell.CommandRequest("run_command", "git status --short\ngit log -1", ["git"], IsScript: true);

        var spec = App.ChatScreen.ApprovalSpec(request);

        Assert.Equal(Shell.ShellText.ScriptApprovalTitle, spec.Title);
        Assert.Equal(Shell.ShellText.WholeCommandLines(request), spec.Lines);
        Assert.Equal([Shell.ShellText.DenyRow, Shell.ShellText.OnceRow, Shell.ShellText.SessionRow(request), Shell.ShellText.PermanentRow(request)], spec.Choices.Select(c => c.Label));
        Assert.Equal(Shell.CommandChoice.Permanent, App.ChatScreen.ApprovalChoice(new WebAnswerRequest { Choice = "permanent" }));
        Assert.Equal(Shell.CommandChoice.Deny, App.ChatScreen.ApprovalChoice(new WebAnswerRequest { Choice = "?" }));
    }

    // ── The session ──────────────────────────────────────────────────────────

    [Fact]
    public void AMessage_Waits_ForTheIdleLine_AndWakesIt()
    {
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Id = "m1", Text = "hello" }, null).Outcome);

        Assert.Equal(1, _nudges);
        Assert.Equal(["hello"], _session.Waiting);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal(new WebLine("hello", "m1"), line);
        Assert.False(_session.LinesReady);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("/clear")]
    [InlineData("/settings")]
    [InlineData("/nosuch thing")]
    public void ABlankOrASlashLine_IsRefused(string text)
    {
        var reply = _session.Send(new WebSendRequest { Text = text }, null);

        Assert.Equal(WebOutcome.Refused, reply.Outcome);
        Assert.False(_session.LinesReady);
    }

    private static App.WebCommandSettings CommandsOn => new(Commands: true, FileTools: true, ObsidianTools: false);

    [Fact]
    public void WithCommandsOn_ACommandWaits_AndAPaneFormIsRefused_WithItsReason()
    {
        _session.CommandSettings = () => CommandsOn;

        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = "/clear" }, IPAddress.Loopback).Outcome);
        var pane = _session.Send(new WebSendRequest { Text = "/settings" }, null);
        var never = _session.Send(new WebSendRequest { Text = "/exit" }, null);

        Assert.Equal((WebOutcome.Refused, WebUiText.OpensPane("/settings", null)), (pane.Outcome, pane.Message));
        Assert.Equal(WebUiText.PcOnly("/exit"), never.Message);
        Assert.False(pane.AsText);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal(new WebLine("/clear", null, IPAddress.Loopback), line);
    }

    [Fact]
    public void AnUnknownCommand_OffersTheMessage_AndTheCommandMeant()
    {
        _session.CommandSettings = () => CommandsOn;

        var unknown = _session.Send(new WebSendRequest { Text = "/nosuch thing" }, null);
        var typo = _session.Send(new WebSendRequest { Text = "//tree src" }, null);
        var asText = _session.Send(new WebSendRequest { Text = "/nosuch thing", AsText = true }, null);

        Assert.Equal((WebOutcome.Refused, WebUiText.UnknownCommand("/nosuch"), true, (string?)null), (unknown.Outcome, unknown.Message, unknown.AsText, unknown.Use));
        Assert.Equal("/tree src", typo.Use);
        Assert.Equal(WebOutcome.Ok, asText.Outcome);
        Assert.True(_session.TryTakeLine(out var line) && line.AsText);
    }

    [Fact]
    public void WithCommandsOff_EveryCommandNamesTheSetting_ButAMessageGoes()
    {
        var command = _session.Send(new WebSendRequest { Text = "/clear" }, null);
        var unknown = _session.Send(new WebSendRequest { Text = "/home/me" }, null);

        Assert.Equal(WebUiText.CommandsOff, command.Message);
        Assert.Equal(WebUiText.CommandsOff, unknown.Message);
        Assert.True(unknown.AsText);   // a path still goes as a message
        Assert.Null(unknown.Use);
    }

    [Fact]
    public void ACommandFromThePage_IsAudited_ByItsWordAlone()
    {
        _session.CommandSettings = () => CommandsOn;
        var lines = new List<string>();
        void Record(NeonSidekick.Diagnostics.DiagnosticEvent e) => lines.Add(e.Message);
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Record;
        try
        {
            _session.Send(new WebSendRequest { Text = "/remember my secret pin" }, IPAddress.Parse("192.168.1.5"));
            _session.Send(new WebSendRequest { Text = "/settings" }, IPAddress.Parse("192.168.1.5"));
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Record;
        }

        Assert.Contains(WebUiText.CommandLogLine("/remember", IPAddress.Parse("192.168.1.5"), "run"), lines);
        Assert.Contains(WebUiText.CommandLogLine("/settings", IPAddress.Parse("192.168.1.5"), "refused"), lines);
        Assert.DoesNotContain(lines, l => l.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void ADraft_GoesToTheConnectedPages_OrWaitsForTheNextSnapshot_OnceEitherWay()
    {
        _session.Draft("held for later");
        Assert.Equal(["held for later"], _session.Snapshot().Drafts!);
        Assert.Null(_session.Snapshot().Drafts);   // delivered once

        using var page = _session.Events.Subscribe();
        _session.Draft("now");
        Assert.True(page.Reader.TryRead(out var item));
        Assert.Equal("draft", item.Name);
        Assert.Equal("now", JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text);
        Assert.Null(_session.Snapshot().Drafts);
    }

    [Fact]
    public void TheHeldLines_AreListedAsWaiting()
    {
        _session.HeldForPc = () => ["/clear"];
        _session.Send(new WebSendRequest { Text = "hello" }, null);

        Assert.Equal(["hello", "/clear"], _session.Waiting);
    }

    [Fact]
    public void APickCard_IsAnsweredByARowsId_AndTheNoticeNamesTheRow()
    {
        var broker = new WebAskBroker();
        var spec = new WebAskSpec("pick", "🤖 Model", [new WebChoice("cancel", WebUiText.CancelChoice)], Items: [new("i0", "llama"), new("i1", "qwen", "the model in use")], Current: "i1");
        var handle = broker.Open(spec);

        Assert.Equal(WebOutcome.Refused, broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "i9" }, null).Outcome);
        Assert.Equal(WebOutcome.Ok, broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "i0" }, null).Outcome);
        Assert.Equal("llama", handle.Describe(new WebAnswerRequest { Choice = "i0" }));
        Assert.Equal(WebUiText.CancelChoice, handle.Describe(new WebAnswerRequest { Choice = "cancel" }));
    }

    [Fact]
    public void AListCard_KeepsItsLastPart_UnderTheCutNote()
    {
        var lines = Enumerable.Range(0, 3000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 200)).ToList();

        var spec = App.ChatScreen.InfoSpec("/tree", lines);

        Assert.Equal(("info", "/tree"), (spec.Kind, spec.Title));
        Assert.Equal(WebUiText.InfoCutNote, spec.Lines![0]);
        Assert.Equal(lines[^1], spec.Lines[^1]);
        Assert.True(spec.Lines.Sum(l => l.Length + 1) <= WebUiText.MaxInfoChars + WebUiText.InfoCutNote.Length + 1);
        Assert.Equal(["close"], spec.Choices.Select(c => c.Id));
        Assert.Equal(["a", "b"], App.ChatScreen.InfoSpec("x", ["a", "b"]).Lines);
    }

    [Fact]
    public void ABareWord_IsAMessage_NotACommand()
    {
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = "clear" }, null).Outcome);
    }

    [Fact]
    public void TheCap_IsInBytes_AndTheQueueHoldsTwenty()
    {
        string wide = new('界', WebUiSession.MaxMessageBytes / 3 + 1);   // three bytes each in UTF-8
        Assert.Equal(WebOutcome.Refused, _session.Send(new WebSendRequest { Text = wide }, null).Outcome);

        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Id = "q" + i, Text = "m" + i }, null).Outcome);
        }

        Assert.Equal(WebOutcome.TooMany, _session.Send(new WebSendRequest { Id = "late", Text = "late" }, null).Outcome);
        Assert.Equal(WebUiSession.MaxWaiting, _session.WaitingCount);
    }

    [Fact]
    public void ARetry_OfAMessageTaken_IsNotSentTwice()
    {
        _session.Send(new WebSendRequest { Id = "same", Text = "once" }, null);
        var again = _session.Send(new WebSendRequest { Id = "same", Text = "once" }, null);

        Assert.Equal(WebOutcome.Ok, again.Outcome);
        Assert.Equal(1, _session.WaitingCount);
    }

    [Fact]
    public void ThePcsPasteStandIns_AreTakenOut()
    {
        string token = new([(char)0xE000]);
        Assert.True(PasteBlocks.IsToken(token[0]));
        _session.Send(new WebSendRequest { Text = "see " + token + " here" }, null);

        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal("see  here", line.Text);
    }

    [Fact]
    public void DroppedAndWithdrawnLines_GoBackToThePage()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Send(new WebSendRequest { Text = "one" }, null);
        _session.Send(new WebSendRequest { Text = "two" }, null);

        Assert.Equal(2, _session.DropLines());
        _session.Withdrawn("three");

        var events = Drain(subscription);
        Assert.Equal(["dropped:one", "dropped:two", "withdrawn:three"], events.Where(e => e.Name != "state").Select(e => e.Name + ":" + JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text));
        Assert.False(_session.LinesReady);
    }

    [Fact]
    public void Notices_WaitForTheScreen_InOrder()
    {
        _session.Notice("a");
        _session.Notice("b", error: true);

        Assert.True(_session.NoticesReady);
        Assert.Equal([("a", false), ("b", true)], _session.TakeNotices());
        Assert.False(_session.NoticesReady);
        Assert.Equal(2, _nudges);
    }

    [Fact]
    public void TheFeed_AndTheAsks_ReachThePages()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Feed.Begin(null);
        _session.Feed.Append("**hi**");
        _session.Feed.End();
        var handle = _session.Asks.Open(YesNo());
        _session.Asks.Close(handle);

        var events = Drain(subscription);
        Assert.Equal(["append", "delta", "end", "asks", "asks"], events.Select(e => e.Name));
        Assert.Contains("<strong>hi</strong>", JsonSerializer.Deserialize(events[2].Json.Value, WebUiJsonContext.Default.WebEnd)!.Html, StringComparison.Ordinal);
        Assert.Contains("\"Go?\"", events[3].Json.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheState_IsSentWhenItChanges_AndOnlyThen()
    {
        using var subscription = _session.Events.Subscribe();

        _time.Advance(WebUiSession.StateInterval);
        _time.Advance(WebUiSession.StateInterval);
        _session.Send(new WebSendRequest { Text = "x" }, null);

        var states = Drain(subscription).Where(e => e.Name == "state").ToList();
        Assert.Equal(2, states.Count);   // the first tick, then the queued line; the second tick changed nothing
    }

    // ── Lighter reconnects (2026-10-15) ─────────────────────────────────────

    [Fact]
    public void ASnapshot_DefersAFinishedThinkingBlock_AndALongFoldedDiff_WhileLiveEventsCarryThemWhole()
    {
        var feed = new TranscriptFeed();
        var changes = Watch(feed);
        feed.AppendThinking("A long think.", TimeSpan.FromSeconds(1));
        var rows = Enumerable.Range(1, TranscriptFeed.DeferRows + 1).Select(i => (WebDiffRow?)new WebDiffRow("+", i, "x")).ToList();
        feed.Add(FeedKind.Tool, "🛠️ edited a.txt", diff: new WebDiff("Added 201 lines", rows, null, 10, "Added 201 lines · 201 rows"));
        feed.Add(FeedKind.Tool, "🛠️ edited b.txt", diff: new WebDiff("Added 1 line", [rows[0]], null, 0, ""));

        var deferred = feed.Snapshot(m => m, defer: true);
        Assert.Equal(("", (bool?)true), (deferred[0].Text, deferred[0].Deferred));
        Assert.NotNull(deferred[0].Summary);
        Assert.Equal((0, (bool?)true), (deferred[1].Diff!.Rows.Count, deferred[1].Deferred));
        Assert.Null(deferred[2].Deferred);   // a short diff goes whole

        Assert.Equal("A long think.", feed.EntryOf(deferred[0].Seq, m => m)!.Text);
        Assert.Equal(TranscriptFeed.DeferRows + 1, feed.EntryOf(deferred[1].Seq, m => m)!.Diff!.Rows.Count);
        Assert.Null(feed.EntryOf(999, m => m));
        Assert.Contains(changes, c => c is FeedChange.Grew g && g.Text == "A long think.");
        Assert.Equal("A long think.", feed.Snapshot(m => m)[0].Text);   // undeferred: whole
    }

    [Fact]
    public void TheSessionsSnapshot_Defers_AndItsEntryReadsWhole()
    {
        _session.Feed.AppendThinking("Hmm.", TimeSpan.FromSeconds(1));
        _session.Feed.Add(FeedKind.Notice, "(done)");
        var thinking = _session.Snapshot().Entries[0];
        Assert.True(thinking.Deferred);
        Assert.Equal("Hmm.", _session.Entry(thinking.Seq)!.Text);
    }

    // ── The theme and the sign-in (2026-10-15) ──────────────────────────────

    [Fact]
    public void TheThemesSheet_IsThePalette_OrNothingWhenOff_AndTheManifestFollows()
    {
        var palette = UI.Theme.Current;
        Assert.Equal("", WebTheme.Css(palette, on: false));
        string css = WebTheme.Css(palette, on: true);
        Assert.Contains("--accent: " + WebTheme.Hex(palette.Primary) + ";", css, StringComparison.Ordinal);
        Assert.Equal(WebTheme.Readable(palette), css.Contains("--bg: ", StringComparison.Ordinal));
        Assert.Contains("\"background_color\": \"" + WebTheme.PageGround + "\"", WebTheme.Manifest(palette, on: false), StringComparison.Ordinal);
        Assert.Contains("/icon-512.png", WebTheme.Manifest(palette, on: true), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBuiltInTheme_IsASheet_ItsInkReadableWhereItLendsItsGrounds()
    {
        foreach (var palette in UI.ThemeLibrary.All)
        {
            string css = WebTheme.Css(palette, on: true);
            Assert.StartsWith("/* The profile's theme, ", css, StringComparison.Ordinal);
            if (css.Contains("--bg: ", StringComparison.Ordinal))
            {
                Assert.True(WebTheme.Contrast(palette.Ink, palette.Bg) >= WebTheme.MinContrast, palette.Name);
            }
        }

        Assert.Equal(21, WebTheme.Contrast(Spectre.Console.Color.White, Spectre.Console.Color.Black), 1);
    }

    [Theory]
    [InlineData("http://192.168.1.217:8765/signin?t=abc_DEF-123", "abc_DEF-123")]
    [InlineData("  abc_DEF-123  ", "abc_DEF-123")]
    [InlineData("http://h:1/signin?x=1&t=tok&y=2", "tok")]
    [InlineData("", "")]
    public void APastedSignIn_IsReadForItsToken(string pasted, string token)
    {
        Assert.Equal(token, WebUiAuth.TokenFrom(pasted));
    }

    // ── The status row (2026-10-15) ─────────────────────────────────────────

    private (WebStatus Status, string Key) _status = (new WebStatus(null, null, null, null), "");

    private void StatusIs(string? label, long since, string? usage)
    {
        _status = (new WebStatus(label, label is null ? null : 1_000, null, usage is null ? null : new WebUsage(usage, null, null)),
            label + "\n" + since + "\n" + usage);
    }

    private List<WebStatus> Statuses(WebEventHub.Subscription subscription) =>
        Drain(subscription).Where(e => e.Name == "status").Select(e => JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebStatus)!).ToList();

    [Fact]
    public void TheStatus_IsSentOnANewLabelOrUsage_NotForAClockTick_AndNeverWithNoPage()
    {
        _session.StatusNow = () => _status;
        StatusIs("thinking", 1, null);
        _session.PublishStatus();   // no page: nothing, and nothing remembered

        using var subscription = _session.Events.Subscribe();
        _session.PublishStatus();
        _session.PublishStatus();   // the same key: a running clock alone sends nothing
        StatusIs("writing", 1, null);
        _session.PublishStatus();
        StatusIs("writing", 1, "1.2k tokens");
        _time.Advance(WebUiSession.StateInterval);   // the sampler's tick sends it too

        var sent = Statuses(subscription);
        Assert.Equal(["thinking", "writing", "writing"], sent.Select(s => s.Label));
        Assert.Equal(1_000, sent[0].ElapsedMs);
        Assert.Equal("1.2k tokens", sent[2].Usage!.Tokens);
    }

    [Fact]
    public void TheStatus_IsThrottled_AndTheLastLabelAlwaysLands()
    {
        _session.StatusNow = () => _status;
        using var subscription = _session.Events.Subscribe();

        StatusIs("thinking", 1, null);
        _session.StatusNudge();
        StatusIs("read_file", 1, null);
        _session.StatusNudge();   // inside the window: held
        StatusIs("grep", 1, null);
        _session.StatusNudge();   // held as well; the trailing send reads the newest
        Assert.Equal(["thinking"], Statuses(subscription).Select(s => s.Label));

        _time.Advance(WebUiSession.StatusThrottle);
        Assert.Equal(["grep"], Statuses(subscription).Select(s => s.Label));
    }

    [Fact]
    public void TheSnapshot_CarriesTheStatus_AndADisposedSessionSendsNoTrailingOne()
    {
        _session.StatusNow = () => _status;
        StatusIs("thinking", 1, "1.2k tokens");
        Assert.Equal("thinking", _session.Snapshot().Status!.Label);

        using var subscription = _session.Events.Subscribe();
        _session.StatusNudge();
        StatusIs("writing", 1, null);
        _session.StatusNudge();   // a trailing send armed
        Drain(subscription);
        _session.Dispose();
        _time.Advance(WebUiSession.StatusThrottle);

        Assert.Empty(Statuses(subscription));
    }

    [Fact]
    public void TheUsageCard_IsTheScreens_ElseNothingCounted()
    {
        Assert.Equal((NeonSidekick.App.UsageText.Label, null, 0, NeonSidekick.App.UsageText.NothingCounted), (_session.Usage().Title, _session.Usage().Context, _session.Usage().Sections.Count, _session.Usage().Empty));
        var card = new WebUsageCard(NeonSidekick.App.UsageText.Label, new WebUsageContext("Context", "1.0k", "8.0k", 13, "configured"), [], null);
        _session.UsageNow = () => card;
        Assert.Same(card, _session.Usage());
    }

    private static List<WebEvent> Drain(WebEventHub.Subscription subscription)
    {
        var events = new List<WebEvent>();
        while (subscription.Reader.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    // ── Shopping ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheShoppingPage_IsTheListInTheAppsOwnWords()
    {
        _list.Add("Milk", 2, null, "whole");
        var bread = _list.Add("Bread", 1, null)!.Item;
        _list.Check(bread, true);

        var page = _session.Shopping();

        Assert.Equal(ShoppingText.List(_list.View(null), withTicked: false).Split('\n')[0], page.Header);
        var milk = Assert.Single(Assert.Single(page.Groups).Items);
        Assert.Equal(("Milk ×2", "whole", false), (milk.Text, milk.Note, milk.Checked));
        Assert.Equal(ShoppingText.UnplacedHeading, page.Groups[0].Heading);
        Assert.Equal(ShoppingText.TickedHeading, page.TickedHeading);
        Assert.Equal("Bread", Assert.Single(page.Ticked).Text);
    }

    [Fact]
    public void ATick_IsExplicit_AndAGoneLineIsNotFound()
    {
        var milk = _list.Add("Milk", 1, null)!.Item;

        Assert.Equal(WebOutcome.Ok, _session.Check(new WebCheckRequest { Id = milk.Id, On = true }, null).Outcome);
        Assert.Equal(WebOutcome.Ok, _session.Check(new WebCheckRequest { Id = milk.Id, On = true }, null).Outcome);   // again: no flip back
        Assert.True(_list.Store.Items().Single().Checked);
        Assert.Equal(1, _shoppingChanges);
        Assert.Equal(WebOutcome.NotFound, _session.Check(new WebCheckRequest { Id = 9999, On = true }, null).Outcome);
    }

    [Fact]
    public void TheShoppingPage_OffersTheStores_WithTheActiveOne()
    {
        Assert.Null(_session.Shopping().Active);
        _list.AddStore("Costco");
        _list.AddStore("Aldi");

        var page = _session.Shopping();

        Assert.Equal(["Aldi", "Costco"], page.Stores!.Select(s => s.Name));   // by name
        Assert.Equal(_list.Store.FindStore("Costco")!.Id, page.Active);   // the first made is active
        Assert.Equal(WebUiText.StoreLabel, page.StoreLabel);
        Assert.Equal(WebUiAssets.Current, page.Assets);
    }

    [Fact]
    public void PickingAStore_SwitchesIt_LogsAndFlashes_AndTheSameOrAGoneOneDoesNothing()
    {
        _list.AddStore("Costco");
        _list.AddStore("Aldi");
        long aldi = _list.Store.FindStore("Aldi")!.Id;
        long costco = _list.Store.FindStore("Costco")!.Id;
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var landed = new SemaphoreSlim(0);
        _session.FlashOnPc = text => { flashed.Enqueue(text); landed.Release(); };
        var lines = new List<string>();
        void Record(NeonSidekick.Diagnostics.DiagnosticEvent e) => lines.Add(e.Message);
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += Record;
        try
        {
            Assert.Equal(WebOutcome.Ok, _session.UseStore(new WebStoreRequest { Id = aldi }, IPAddress.Loopback).Outcome);
            Assert.Equal(aldi, _list.Store.ActiveStore()!.Id);
            Assert.Equal(1, _shoppingChanges);
            Assert.Contains(WebUiText.StoreLogLine("Aldi", IPAddress.Loopback), lines);
            Assert.True(landed.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(WebUiText.StoreFlash("Aldi"), Assert.Single(flashed));

            Assert.Equal(WebOutcome.Ok, _session.UseStore(new WebStoreRequest { Id = aldi }, null).Outcome);   // already: nothing
            Assert.Equal(1, _shoppingChanges);
            var gone = _session.UseStore(new WebStoreRequest { Id = 9999 }, null);
            Assert.Equal((WebOutcome.NotFound, WebUiText.NoSuchStore), (gone.Outcome, gone.Message));
            Assert.Equal(aldi, _list.Store.ActiveStore()!.Id);
            Assert.NotEqual(aldi, costco);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= Record;
        }
    }

    [Fact]
    public void ATick_FlashesOnThePc_OffTheRequestsThread()
    {
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var landed = new SemaphoreSlim(0);
        int requestThread = Environment.CurrentManagedThreadId;
        int flashThread = requestThread;
        _session.FlashOnPc = text =>
        {
            flashThread = Environment.CurrentManagedThreadId;
            flashed.Enqueue(text);
            landed.Release();
        };
        var milk = _list.Add("Milk", 1, null)!.Item;

        Assert.Equal(WebOutcome.Ok, _session.Check(new WebCheckRequest { Id = milk.Id, On = true }, null).Outcome);

        Assert.True(landed.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(WebUiText.CheckedFlash("Milk", on: true), Assert.Single(flashed));
        Assert.NotEqual(requestThread, flashThread);
        Assert.StartsWith(WebUiText.Glyph, WebUiText.CheckedFlash("Milk", on: false), StringComparison.Ordinal);
    }

    [Fact]
    public void AFlash_WhileThePcHoldsTheLast_ReturnsAtOnce_AndOnlyTheNewestLandsAfter()
    {
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var first = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var done = new CountdownEvent(2);
        _session.FlashOnPc = text =>
        {
            flashed.Enqueue(text);
            first.Set();
            release.Wait(TimeSpan.FromSeconds(10));   // the PC's console stalled under its lock
            done.Signal();
        };

        _session.FlashPc("one");
        Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
        _session.FlashPc("two");
        _session.FlashPc("three");   // returns at once, held behind the stalled one
        release.Set();

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(["one", "three"], flashed);
    }

    // ── The words, the token, the reply's HTML ───────────────────────────────

    [Fact]
    public void TheReach_ReadsItsTwoWords()
    {
        Assert.Equal(["local", "lan"], WebUiReach.Names);
        Assert.Equal("lan", WebUiReach.Default);
        Assert.True(WebUiReach.TryParse(" LOCAL ", out var local) && local == WebUiAudience.Local);
        Assert.False(WebUiReach.TryParse("internet", out var fallback));
        Assert.Equal(WebUiAudience.LocalAreaNetwork, fallback);
        Assert.Equal(WebUiAudience.LocalAreaNetwork, WebUiReach.Resolve(new Settings.AppSettingsData { WebUiReach = "nope" }));
    }

    [Fact]
    public void TheToken_TheCookie_AndTheLink()
    {
        string token = WebUiAuth.NewToken();
        Assert.True(WebUiAuth.IsWellFormed(token));
        Assert.NotEqual(token, WebUiAuth.NewToken());
        Assert.True(WebUiAuth.Matches(token, token));
        Assert.False(WebUiAuth.Matches(token[..^1] + "0", token) && token[^1] != '0');
        Assert.False(WebUiAuth.Matches("", ""));
        Assert.False(WebUiAuth.Matches(null, token));
        Assert.Equal("neon_webui_8765", WebUiAuth.CookieName(8765));
        Assert.Equal("http://192.168.1.5:8765/signin?t=abc", WebUiAuth.SignInLink("192.168.1.5", 8765, "abc"));
        Assert.Equal("http://[fd00::1]:8765/", WebUiAuth.Address("fd00::1", 8765));
        Assert.Equal("on lan:8765", new WebUiWanted(true, 8765, WebUiAudience.LocalAreaNetwork, "secret").ToString());   // never the token
    }

    [Fact]
    public void AReplysHtml_KeepsSafeLinksOnly_AndNoPicture()
    {
        string html = WebUiMarkdown.ToHtml("[ok](https://example.com/a) [bad](javascript:alert(1)) ![pic](http://tracker/x.png) <b>raw</b> <https://auto.example>");

        Assert.Contains("href=\"https://example.com/a\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("bad", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://auto.example\"", html, StringComparison.Ordinal);
        Assert.Null(WebUiMarkdown.SafeLink("file:///c:/x"));
        Assert.Equal("mailto:a@b.c", WebUiMarkdown.SafeLink("mailto:a@b.c"));
    }

    // ── The settings ─────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeRows_AreRefusedUnderAReply_AndRestartTheServer()
    {
        foreach (var field in new[] { App.SettingsField.WebUi, App.SettingsField.WebUiPort, App.SettingsField.WebUiReach })
        {
            Assert.True(App.SettingsMenu.IsWebUiField(field));
            Assert.True(App.SettingsMenu.RefusedMidTurn(field));
        }

        Assert.True(App.SettingsMenu.IsToggle(App.SettingsField.WebUi));
        Assert.Equal("must be a port from 1024 to 65535", App.SettingsMenu.WebUiPortRangeError);
        // Server, Guardrails, Tabs (2026-10-16, the user's ask).
        Assert.Equal([App.SettingsField.WebUi, App.SettingsField.WebUiPort, App.SettingsField.WebUiReach, App.SettingsField.WebUiCommands, App.SettingsField.WebUiConnectionDetails,
            App.SettingsField.WebUiBotCheckControl, App.SettingsField.WebUiProfileTheme, App.SettingsField.WebUiChatControls, App.SettingsField.WebUiHomeControls, App.SettingsField.WebUiShoppingControls, App.SettingsField.WebUiYouTubeControls],
            App.SettingsMenu.TabFields[(int)App.SettingsTab.WebUi]);
        // The Guardrails and Tabs rows (later on 2026-10-14; the Home tab's and the theme's 2026-10-15; the Chat and Shopping tabs' later that day) are read at each
        // request: none restarts the server or is refused under a reply.
        foreach (var field in new[]
        {
            App.SettingsField.WebUiChatControls, App.SettingsField.WebUiCommands, App.SettingsField.WebUiConnectionDetails, App.SettingsField.WebUiHomeControls,
            App.SettingsField.WebUiShoppingControls, App.SettingsField.WebUiYouTubeControls, App.SettingsField.WebUiProfileTheme, App.SettingsField.WebUiBotCheckControl,
        })
        {
            Assert.False(App.SettingsMenu.IsWebUiField(field));
            Assert.False(App.SettingsMenu.RefusedMidTurn(field));
            Assert.True(App.SettingsMenu.IsToggle(field));
        }

        Assert.Equal((false, false), (new Settings.AppSettingsData().WebUiCommands, new Settings.AppSettingsData().WebUiConnectionDetails));   // the details off since 2026-10-16, the user's call
        // Later on 2026-10-15, the user's calls: Chat on, Home and Shopping off.
        Assert.Equal((true, false, false), (new Settings.AppSettingsData().WebUiChatControls, new Settings.AppSettingsData().WebUiHomeControls, new Settings.AppSettingsData().WebUiShoppingControls));
        Assert.False(new Settings.AppSettingsData().WebUiYouTubeControls);   // 2026-10-16, the user's call: off, like Home and Shopping
        Assert.True(new Settings.AppSettingsData().WebUiBotCheckControl);    // 2026-10-17, the user's call: on
        Assert.Equal("Web UI", App.SettingsMenu.TabTitles[(int)App.SettingsTab.WebUi]);
        var defaults = new Settings.AppSettingsData();
        Assert.Equal((false, 8765, "lan", ""), (defaults.WebUi, defaults.WebUiPort, defaults.WebUiReach, defaults.WebUiToken));
        Assert.Contains(nameof(Settings.AppSettingsData.WebUiToken), Settings.SettingsDiff.Secrets);
    }

    [Fact]
    public void ACopiedProfile_KeepsItsOwnToken_AndANewOneHasNone()
    {
        Settings.Profiles.Create(_dir, "from", new Settings.AppSettingsData { WebUi = true, WebUiToken = "dpapi:from" });
        Settings.Profiles.Create(_dir, "to", new Settings.AppSettingsData { WebUiToken = "dpapi:to" });

        Settings.Profiles.CopySettings(_dir, "from", "to");
        Settings.Profiles.CopySettings(_dir, "from", "fresh");

        var to = Settings.Profiles.ReadProfileFile(Settings.Profiles.ProfileFile(_dir, "to"));
        Assert.True(to.WebUi);   // the rest copied
        Assert.Equal("dpapi:to", to.WebUiToken);
        Assert.Equal("", Settings.Profiles.ReadProfileFile(Settings.Profiles.ProfileFile(_dir, "fresh")).WebUiToken);
    }

    // ── The sign-in addresses ────────────────────────────────────────────────

    private static WebUiAdapter Adapter(System.Net.NetworkInformation.NetworkInterfaceType type, string address, bool gateway, long speed = 1_000_000_000, string names = "") =>
        new(type, [IPAddress.Parse(address)], gateway, speed, names);

    private const System.Net.NetworkInformation.NetworkInterfaceType Tunnel = (System.Net.NetworkInformation.NetworkInterfaceType)53;

    private static List<string> Hosts(IReadOnlyList<WebUiAddress> addresses) => [.. addresses.Select(a => a.Host)];

    [Fact]
    public void Tailscale_ThenTheHomeNetwork_NotNordLynxNorWsl()
    {
        // This machine on 2026-10-14 (the user's question: three addresses): NordLynx and Tailscale are tunnels (a virtual type), WSL's switch
        // is Ethernet with no gateway, and the Realtek card is the home network; Tailscale's own address is marked (later that day), first since
        // 2026-10-17 (the user's ask), each adapter named as Get-NetAdapter names it (Name and InterfaceDescription).
        var adapters = new[]
        {
            Adapter(Tunnel, "10.5.0.2", gateway: false, names: "NordLynx NordLynx Tunnel"),
            Adapter(Tunnel, "100.78.20.125", gateway: false, names: "Tailscale Tailscale Tunnel"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.17.224.1", gateway: false, names: "vEthernet (WSL (Hyper-V firewall)) Hyper-V Virtual Ethernet Adapter"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true, speed: 2_500_000_000, names: "Ethernet Realtek Gaming 2.5GbE Family Controller"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Loopback, "127.0.0.1", gateway: false, names: "Loopback Pseudo-Interface 1"),
        };

        Assert.Equal([new WebUiAddress("100.78.20.125", true), new WebUiAddress("192.168.1.217", false)], WebUiController.ChooseAddresses(adapters));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("100.64.0.1")));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("100.127.255.254")));
        Assert.False(WebUiController.IsTailnet(IPAddress.Parse("100.128.0.1")));
        Assert.False(WebUiController.IsTailnet(IPAddress.Parse("10.5.0.2")));
    }

    /// <summary>A tailnet-range address that is not on Tailscale's adapter (later on 2026-10-14, the user's pick): another VPN's, or a provider's carrier-grade NAT, gets no tab.</summary>
    [Fact]
    public void ATailnetRangeAddress_OffTailscalesAdapter_GetsNoTab()
    {
        var adapters = new[]
        {
            Adapter(Tunnel, "100.70.1.2", gateway: false, names: "NordLynx NordLynx Tunnel"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "100.80.3.4", gateway: false, names: "Ethernet 2 Realtek USB GbE Family Controller"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true, names: "Ethernet Realtek Gaming 2.5GbE Family Controller"),
        };

        Assert.Equal([new WebUiAddress("192.168.1.217", false)], WebUiController.ChooseAddresses(adapters));
    }

    [Fact]
    public void TailscalesAdapter_ByNameOrDescription_AndAMacsUtun()
    {
        static WebUiAdapter Named(string names, System.Net.NetworkInformation.NetworkInterfaceType type = Tunnel) => Adapter(type, "100.78.20.125", gateway: false, names: names);
        Assert.True(WebUiController.IsTailscaleAdapter(Named("Tailscale Tailscale Tunnel"), windows: true));
        Assert.True(WebUiController.IsTailscaleAdapter(Named("Ethernet 3 Tailscale Tunnel"), windows: true));   // renamed: the description still says so
        Assert.True(WebUiController.IsTailscaleAdapter(Named("tailscale0 tailscale0"), windows: false));
        Assert.False(WebUiController.IsTailscaleAdapter(Named("Ethernet Realtek Gaming 2.5GbE Family Controller", System.Net.NetworkInformation.NetworkInterfaceType.Ethernet), windows: true));
        Assert.False(WebUiController.IsTailscaleAdapter(Named("utun4 utun4"), windows: true));
        Assert.True(WebUiController.IsTailscaleAdapter(Named("utun4 utun4"), windows: false));   // macOS names Tailscale's tunnel utunN alone
        Assert.False(WebUiController.IsTailscaleAdapter(Named(""), windows: false));
    }

    [Fact]
    public void TailscaleDisconnected_LeavesTheHomeNetworkAlone()
    {
        var adapters = new[]
        {
            Adapter(Tunnel, "169.254.83.107", gateway: false, names: "Tailscale Tailscale Tunnel"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true),
        };

        Assert.Equal([new WebUiAddress("192.168.1.217", false)], WebUiController.ChooseAddresses(adapters));
    }

    [Fact]
    public void WiredAndWireless_BothShow_TheFasterFirst()
    {
        var adapters = new[]
        {
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "192.168.1.50", gateway: true, speed: 300_000_000),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true, speed: 2_500_000_000),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "8.8.8.8", gateway: true),   // not private: never
        };

        var chosen = WebUiController.ChooseAddresses(adapters);
        Assert.Equal(["192.168.1.217", "192.168.1.50"], Hosts(chosen));
        Assert.DoesNotContain(chosen, a => a.Tailscale);
    }

    [Fact]
    public void WithNoHomeAdapter_EveryPrivateAddressIsOffered_TailscalesMarked()
    {
        var adapters = new[]
        {
            Adapter(Tunnel, "100.101.102.103", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.17.224.1", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "169.254.1.1", gateway: false),
        };

        Assert.Equal([new WebUiAddress("100.101.102.103", false), new WebUiAddress("172.17.224.1", false)], WebUiController.ChooseAddresses(adapters));   // an unnamed tunnel's 100.x: listed, unmarked
        adapters[0] = Adapter(Tunnel, "100.101.102.103", gateway: false, names: "Tailscale Tailscale Tunnel");
        Assert.Equal([new WebUiAddress("100.101.102.103", true), new WebUiAddress("172.17.224.1", false)], WebUiController.ChooseAddresses(adapters));
        Assert.Empty(WebUiController.ChooseAddresses([]));

        // Tailscale's first in the fallback too (2026-10-17), though its adapter comes after another.
        var later = new[]
        {
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.17.224.1", gateway: false),
            Adapter(Tunnel, "100.101.102.103", gateway: false, names: "Tailscale Tailscale Tunnel"),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "10.0.0.9", gateway: false),
        };
        Assert.Equal([new WebUiAddress("100.101.102.103", true), new WebUiAddress("172.17.224.1", false), new WebUiAddress("10.0.0.9", false)], WebUiController.ChooseAddresses(later));
    }

    // ── What the pane says (later on 2026-10-14) ──────────────────────────────

    [Fact]
    public void TheReach_IsDescribedInPlainWords()
    {
        Assert.Equal("localhost only", WebUiReach.Describe("local"));
        Assert.Equal("your home network and your tailnet", WebUiReach.Describe("lan"));
        Assert.Equal("", WebUiReach.Describe("wan"));
    }

    [Fact]
    public void TailscalesTab_IsHeadedByItsNote_AndItsScanHintNamesNoNetwork()
    {
        Assert.Equal("Tailscale: works from any device on your tailnet, at home or away.", WebUiText.TailscaleNote);
        Assert.Equal(WebUiText.TailscaleNote + "\nWeb UI: on\n\n" + WebUiText.TailscaleScanHint + "\n", WebUiText.LinkTabText("Web UI: on", tailscale: true));
        Assert.Equal("Web UI: on\n\n" + WebUiText.ScanHint + "\n", WebUiText.LinkTabText("Web UI: on", tailscale: false));
        Assert.Contains("(on the same network)", WebUiText.ScanHint);
        Assert.DoesNotContain("same network", WebUiText.TailscaleScanHint);
    }

    [Fact]
    public void WithoutAPane_TheLinesMarkTailscalesLink_AndItsLeadNamesTheTailnet()
    {
        var lines = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork,
            [new WebUiLink("http://192.168.1.217:8765/signin?t=x", false), new WebUiLink("http://100.78.20.125:8765/signin?t=x", true)]);
        Assert.Contains(lines, l => l.StartsWith("Open one of these in the browser (a phone on the same network, or on your tailnet for the one marked Tailscale).", StringComparison.Ordinal));
        Assert.Contains("  http://192.168.1.217:8765/signin?t=x", lines);
        Assert.Contains("  http://100.78.20.125:8765/signin?t=x" + WebUiText.TailscaleMark, lines);
        Assert.Equal(" (Tailscale)", WebUiText.TailscaleMark);

        var plain = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, [new WebUiLink("http://192.168.1.217:8765/signin?t=x", false)]);
        Assert.Contains(plain, l => l.StartsWith("Open one of these in the browser (a phone on the same network). ", StringComparison.Ordinal));
        Assert.DoesNotContain(plain, l => l.Contains("Tailscale", StringComparison.Ordinal));
    }

    // ── The controller ───────────────────────────────────────────────────────

    private sealed class FakeHost : IWebUiHost
    {
        public static List<FakeHost> Made { get; } = [];

        public WebUiBinding? Started { get; private set; }

        public bool Stopped { get; private set; }

        public Exception? Fail { get; init; }

        public int Port => Started?.Port ?? 0;

        public Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Started = binding;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TheController_StartsStopsAndRestarts_AsTheSettingsSay()
    {
        var made = new List<FakeHost>();
        await using var controller = new WebUiController(_ => { var host = new FakeHost(); made.Add(host); return host; }, _session);

        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t1"));
        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t1"));   // the same: nothing
        Assert.Single(made);
        Assert.Equal(new WebUiBinding(WebUiAudience.Local, 8765, "t1"), made[0].Started);
        Assert.True(controller.Serving);
        Assert.Empty(_session.TakeNotices());   // a start writes nothing in the transcript (2026-10-17, the user's ask)
        Assert.Equal([new WebUiLink("http://localhost:8765/signin?t=t1", false)], controller.Links("t1"));   // localhost is never Tailscale's

        await controller.ReconcileAsync(new WebUiWanted(true, 8766, WebUiAudience.Local, "t1"));
        Assert.True(made[0].Stopped);
        Assert.Equal(8766, made[1].Started!.Port);

        await controller.ReconcileAsync(WebUiWanted.Off);
        Assert.True(made[1].Stopped);
        Assert.False(controller.Serving);
        Assert.Contains(_session.TakeNotices(), n => n.Text == WebUiText.StoppedNotice);
    }

    [Fact]
    public async Task APortThatCannotBeOpened_IsOneError_AndNotTriedAgainUntilTheSettingsChange()
    {
        int made = 0;
        await using var controller = new WebUiController(_ => { made++; return new FakeHost { Fail = new IOException("Failed to bind") }; }, _session);
        var wanted = new WebUiWanted(true, 8765, WebUiAudience.LocalAreaNetwork, "t");

        await controller.ReconcileAsync(wanted);
        await controller.ReconcileAsync(wanted);

        Assert.Equal(1, made);
        Assert.False(controller.Serving);
        var notice = Assert.Single(_session.TakeNotices());
        Assert.True(notice.Error);
        Assert.Equal(WebUiText.PortFailedError(8765, "Failed to bind"), notice.Text);
    }

    [Fact]
    public async Task WithoutAFactory_ThereIsNoWebUi()
    {
        await using var controller = new WebUiController(null, _session);

        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t"));

        Assert.False(controller.Available);
        Assert.False(controller.Serving);
        Assert.Empty(_session.TakeNotices());
    }
}
