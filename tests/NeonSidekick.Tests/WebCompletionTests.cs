using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The completion lists' router shared by the PC's line and the Web UI's dropdown (later on 2026-10-14), and the dropdown's snapshot: what the page
/// may run alone offered, the connections' notes as the setting says, the inserts, the caps.
/// </summary>
public sealed class WebCompletionTests
{
    private static readonly CompletionLists All = new(true, true, true, true, true, true, true, true);

    private static readonly WebCommandSettings On = new(Commands: true, FileTools: true, ObsidianTools: false);

    private static CompletionSources Sources(Func<string, string, ArgumentList>? arguments = null, Func<string, MentionResult>? mentions = null) => new(
        () => SlashCommands.Completions,
        arguments ?? ((_, _) => ArgumentList.None),
        () => [new("summarize", "sums things up")],
        () => [new("web_search", "searches")],
        () => [new("prod", "sql · server=db01")],
        () => [new("pony", "text to image")],
        () => [new("nas", "\\\\nas\\share")],
        mentions ?? (_ => new MentionResult(FileOutcome.Ok, ["docs/", "notes.md"], false)));

    [Theory]
    [InlineData("/tr", 3, CompletionKind.Command, 0, 3, "/tr", '\0')]
    [InlineData("/tree sr", 8, CompletionKind.Argument, 6, 8, "sr", '\0')]
    [InlineData("see @no", 7, CompletionKind.Mention, 4, 7, "no", '@')]
    [InlineData("use #sum", 8, CompletionKind.Mention, 4, 8, "sum", '#')]
    [InlineData("ask %pr", 7, CompletionKind.Mention, 4, 7, "pr", '%')]
    [InlineData("/loop 5m append to @no", 22, CompletionKind.Mention, 19, 22, "no", '@')]
    public void Route_FindsWhatTheWordUnderTheCursorIs(string text, int cursor, CompletionKind kind, int start, int end, string query, char trigger)
    {
        var route = CompletionQuery.Route(text, cursor, All, command => command == "/tree");

        Assert.NotNull(route);
        Assert.Equal((kind, start, end, query, trigger), (route.Kind, route.Start, route.End, route.Query, route.Trigger));
    }

    [Fact]
    public void Route_IsNothing_ForAPlainWord_OrAListSwitchedOff()
    {
        Assert.Null(CompletionQuery.Route("hello there", 5, All, null));
        Assert.Null(CompletionQuery.Route("see @no", 7, All with { At = false }, null));
        Assert.Null(CompletionQuery.Route("/tr", 3, All with { Commands = false }, null));
    }

    [Fact]
    public void Fetch_NarrowsTheWords_AndAnswersPaths()
    {
        var commands = CompletionQuery.Fetch(new CompletionRoute(CompletionKind.Command, 0, 3, "/tr"), Sources())!;
        var paths = CompletionQuery.Fetch(new CompletionRoute(CompletionKind.Mention, 4, 7, "no", Trigger: '@'), Sources())!;

        Assert.Contains("/tree", commands.Matches);
        Assert.True(commands.IsWordList);
        Assert.False(paths.IsWordList);
        Assert.Equal("@", paths.Prefix);
        Assert.Null(CompletionQuery.Fetch(new CompletionRoute(CompletionKind.Mention, 4, 7, "zz", Trigger: '#'), Sources()));
    }

    private static WebCompletionSnapshot Snapshot(WebCommandSettings? settings = null, bool details = true, Func<string, string, ArgumentList>? arguments = null, bool remains = false) =>
        new(Sources(arguments), All, command => command == "/tree", remains, settings ?? On, details);

    [Fact]
    public void TheDropdown_OffersOnlyTheCommandsThePageMayRun()
    {
        var answer = Snapshot().Complete("/", 1, 7);

        var texts = answer.Items.Select(i => i.Text).ToList();
        Assert.Equal(7, answer.Seq);
        Assert.Contains("/tree", texts);
        Assert.Contains("/model", texts);
        Assert.DoesNotContain("/settings", texts);
        Assert.DoesNotContain("/exit", texts);
        Assert.DoesNotContain("/webui", texts);
        Assert.Equal("/tree ", answer.Items.First(i => i.Text == "/tree").Insert);
        Assert.Equal((0, 1), (answer.Start, answer.End));
    }

    [Fact]
    public void WithCommandsOff_TheDropdownOffersTheMentionsAlone()
    {
        var off = Snapshot(On with { Commands = false });

        Assert.Empty(off.Complete("/", 1, 1).Items);
        Assert.Empty(off.Complete("/browser s", 10, 1).Items);
        Assert.Equal(["#summarize "], off.Complete("use #su", 7, 1).Items.Select(i => i.Insert));
    }

    [Fact]
    public void AnArgument_IsOffered_WhenItsFormRunsFromThePage_OrDoesWithMoreWords()
    {
        var snapshot = Snapshot(arguments: (command, _) => command switch
        {
            "/browser" => new ArgumentList([new("save", "save"), new("list", "the list"), new("unsave", "take one off"), new("clear", "clear")]),
            "/shopping" => new ArgumentList([new("add", "add items"), new("clear", "remove the ticked")]),
            _ => ArgumentList.None,
        });

        Assert.Equal(["save", "unsave"], snapshot.Complete("/browser ", 9, 1).Items.Select(i => i.Text));
        Assert.Equal(["add", "clear"], snapshot.Complete("/shopping ", 10, 1).Items.Select(i => i.Text));
        Assert.Empty(snapshot.Complete("/settings ", 10, 1).Items);   // a command the page may not run lists no words
    }

    [Fact]
    public void TheConnectionsNotes_FollowTheSetting()
    {
        Assert.Equal("sql · server=db01", Snapshot().Complete("ask %p", 6, 1).Items.Single().Note);
        Assert.Null(Snapshot(details: false).Complete("ask %p", 6, 1).Items.Single().Note);
        Assert.Null(Snapshot(details: false).Complete("on *n", 5, 1).Items.Single().Note);
        Assert.Equal("sums things up", Snapshot(details: false).Complete("use #s", 6, 1).Items.Single().Note);
    }

    [Theory]
    [InlineData(false, "@docs/ ")]
    [InlineData(true, "@docs/")]
    public void AFolderPick_StaysOpen_UnderTheRemainMode(bool remains, string insert)
    {
        var items = Snapshot(remains: remains).Complete("see @do", 7, 1).Items;

        Assert.Equal(insert, items.First(i => i.Text == "docs/").Insert);
        Assert.Equal("@notes.md ", items.First(i => i.Text == "notes.md").Insert);
    }

    [Fact]
    public void TheCaps_CutTheText_AndTheRows()
    {
        Assert.True(Snapshot().Complete(new string('a', WebCompletionSnapshot.MaxText + 1), 5, 1).Truncated);
        var many = new WebCompletionSnapshot(Sources(mentions: _ => new MentionResult(FileOutcome.Ok, [.. Enumerable.Range(0, 500).Select(i => "f" + i)], false)), All, null, false, On, true);

        var answer = many.Complete("@f", 2, 1);

        Assert.Equal(WebCompletionSnapshot.MaxItems, answer.Items.Count);
        Assert.True(answer.Truncated);
    }

    [Fact]
    public void TheSession_AnswersFromItsSnapshot_AndQuietlyWhenAListFails()
    {
        using var session = new WebUiSession(() => new WebState("p", "", "", "m", false, false, false, "", [], null, "1"), _ => WebReply.Ok, () => null, () => { });

        Assert.Empty(session.Complete(new WebCompleteRequest { Text = "/", Cursor = 1, Seq = 3 }).Items);   // no snapshot yet
        session.Completions = Snapshot();
        Assert.NotEmpty(session.Complete(new WebCompleteRequest { Text = "/", Cursor = 1, Seq = 4 }).Items);
        session.Completions = new WebCompletionSnapshot(Sources(mentions: _ => throw new IOException("gone")), All, null, false, On, true);
        var failed = session.Complete(new WebCompleteRequest { Text = "@x", Cursor = 2, Seq = 5 });
        Assert.Equal((5L, 0), (failed.Seq, failed.Items.Count));
    }
}
