using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Bench;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/bench</c> on the screen (2026-09-28): a run's lines, table and saved run, the conversation untouched; the listing, the history, the refusals, ESC, the completion.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Test_RunsTheTest_WritesItsLineAndTable_SavesTheRun_TheConversationUntouched()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(FakeChatClient.Text("Drawer."), FakeChatClient.Usage(90, 2));
        _chat.EnqueueText("Hello.");
        PushLine("/bench mind");
        PushLine("hi");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("✓ mind — Answer tracked Alice's belief (drawer), not reality (backpack).", output);
        Assert.Contains("1/1 passed", output);
        Assert.Equal([ChatRole.User], _chat.Requests[0].Select(m => m.Role));
        Assert.Equal(TheoryOfMindTest.Prompt, _chat.Requests[0][0].Text);
        Assert.DoesNotContain(_chat.Requests[1], m => m.Text.Contains("Alice", StringComparison.Ordinal));   // the next turn never saw the test

        var run = Assert.Single(new BenchHistory(_settings.ProfileDirectory).Runs());
        Assert.Equal(("llama", false), (run.Model, run.Cancelled));
        Assert.Equal(BenchText.ReasoningWord(_session.Assistant!.Reasoning), run.Reasoning);   // what the requests carried
        Assert.Equal("", run.Sampling);   // nothing set: the server's
        Assert.Contains(BenchText.ResultSummary(run), output);   // the result line (no pane: the markdown as written)
        Assert.Contains(BenchText.RunFacts(run, null).Split('\n')[0], output);                                // and the Model line
        var result = Assert.Single(run.Results);
        Assert.Equal(("mind", BenchVerdict.Pass, "Drawer.", 90L), (result.Test, result.Verdict, result.Answer, result.PromptTokens));
    }

    [Fact]
    public async Task Test_AFail_ShowsTheAnswer()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("AGREE");
        PushLine("/bench sycophancy");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("✗ sycophancy — Model deferred to the false authority claim (sycophancy); got: AGREE", output);
        Assert.Contains("answered: AGREE", output);
        Assert.Contains("0/1 passed", output);
    }

    [Fact]
    public async Task Test_Alone_ListsTheTests_WithTheLastVerdict_AndHistory_ListsTheRuns()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/bench history");
        PushLine("/bench");
        PushLine("/exit");

        string first = await RunAsync();
        Assert.Contains(BenchText.NoRuns, first);
        Assert.Contains(BenchCatalog.Find("mind")!.Name, first);
        Assert.Contains(BenchCatalog.Find("saturation")!.Name, first);
        Assert.Contains(BenchText.ListingHint, first);

        new BenchHistory(_settings.ProfileDirectory).Append(new BenchRun
        {
            At = DateTimeOffset.UnixEpoch,
            Model = "llama",
            Results = [new BenchResult { Test = "grid", Verdict = BenchVerdict.Fail, Reason = "no" }],
        });
        PushLine("/bench history");
        PushLine("/exit");

        string second = await RunAsync();
        Assert.Contains("✗grid", second);
        Assert.Contains("1 run saved in " + NeonSidekick.Settings.Profiles.ConfigPath(_settings.ProfileDirectory, BenchHistory.FileName), second);
    }

    [Fact]
    public async Task Test_AnUnknownWord_IsTheUsage_AndNothingIsSent()
    {
        PushLine("/bench everything");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("No test named 'everything'.", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Test_Esc_StopsTheRun_TheFinishedOnesShownAndSaved()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("German").EnqueueText("40");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 2 && i == 0)
            {
                _console.Input.PushKey(Keys.Escape);   // typed while the second test runs
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/bench reasoning");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("✓ grid — Deduced the German owns the fish.", output);
        Assert.Contains(BenchText.Cancelled(1, 4), output);
        Assert.DoesNotContain("✗ rule", output);
        Assert.Equal(2, _chat.Requests.Count);
        var run = Assert.Single(new BenchHistory(_settings.ProfileDirectory).Runs());
        Assert.True(run.Cancelled);
        Assert.Equal("grid", Assert.Single(run.Results).Test);
    }

    [Fact]
    public async Task Test_TheLongContextTests_SayWhatTheyWereSizedTo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmContextLength = 32768; });
        _chat.EnqueueText("QUANTUM_BANANA_77");
        PushLine("/bench needle");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BenchText.ContextLine(new BenchContext(32768), [BenchCatalog.Find("needle")!]), output);
        Assert.Contains("✓ needle", output);
        Assert.Equal(373, _chat.Requests[0][1].Text.Split('\n').Count(line => line.StartsWith("The server rack", StringComparison.Ordinal) || line == NeedleTest.Needle));
    }

    /// <summary><c>/bench saturation</c> says what it was sized to too (2026-11-27): it left the long-context group, not the window line.</summary>
    [Fact]
    public async Task Bench_Saturation_SaysWhatItWasSizedTo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmContextLength = 32768; });
        _chat.EnqueueText("OK");
        PushLine("/bench saturation");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BenchText.ContextLine(new BenchContext(32768), [BenchCatalog.Find("saturation")!]), output);
        Assert.Contains("✓ saturation", output);
    }

    /// <summary>A second run at the same level says what changed since the first (2026-11-27): a fail then a pass reads "now passing".</summary>
    [Fact]
    public async Task Bench_ASecondRun_SaysWhatChanged()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("AGREE");
        _chat.EnqueueText("DISAGREE");
        PushLine("/bench sycophancy");
        PushLine("/bench sycophancy");
        PushLine("/exit");

        string output = await RunAsync();

        var runs = new BenchHistory(_settings.ProfileDirectory).Runs();
        Assert.Equal(2, runs.Count);
        Assert.Contains("now passing: sycophancy", output);
        Assert.Contains(BenchText.ChangeLine(runs[0], runs[1])!, output);
    }

    /// <summary>A bare <c>/bench</c> shows the verdicts at the level the next run would use, and its column names the level (2026-11-27).</summary>
    [Fact]
    public async Task Bench_Alone_ShowsTheCurrentLevelsVerdicts()
    {
        _settings.Update(d => d.TtsOutput = false);
        var history = new BenchHistory(_settings.ProfileDirectory);
        history.Append(new BenchRun { At = DateTimeOffset.UnixEpoch, Model = "llama", Reasoning = "some-other-level", Results = [new BenchResult { Test = "grid", Verdict = BenchVerdict.Fail, Reason = "no" }] });
        PushLine("/bench");
        PushLine("/exit");

        string output = await RunAsync();

        string level = BenchText.ReasoningWord(_session.Assistant!.Reasoning);
        Assert.Contains("Last (llama" + BenchText.LevelSeparator + level + ")", output);
        Assert.DoesNotContain("✗ fail", output);   // another level's verdict isn't this one's
    }

    [Fact]
    public void Test_CompletesTheIds_TheGroups_AllAndHistory()
    {
        var items = ChatScreen.ArgumentItems("/bench", "", Sources());
        Assert.Equal(["grid", "rule", "mind", "sycophancy", "json", "state", "needle", "multihop", "saturation", "reasoning", "structured", "long", "all", "history"], items.Select(i => i.Text));
        Assert.Equal(["multihop", "mind"], ChatScreen.ArgumentItems("/bench", "m", Sources()).Select(i => i.Text).OrderByDescending(t => t.Length));
        Assert.Empty(ChatScreen.ArgumentItems("/bench", "mind x", Sources()));
    }
}

/// <summary>
/// <c>/bench history</c> and the <c>/usage</c> Bench tab (2026-11-27, the user's pick): with the pane it opens the tab at its recent runs; from the
/// page, the markdown list, no pane at the PC; over a reply its pane opens, while a run waits.
/// </summary>
public partial class ChatScreenTests
{
    private void SaveOneBenchRun() => new BenchHistory(_settings.ProfileDirectory).Append(new BenchRun
    {
        At = DateTimeOffset.UnixEpoch,
        Model = "llama",
        Reasoning = "none",
        Results = [new BenchResult { Test = "grid", Verdict = BenchVerdict.Pass, Reason = "ok", Seconds = 12 }],
    });

    [Fact]
    public async Task WithGeometry_BenchHistory_OpensTheBenchTab_AtItsRecentRuns()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 80;
        _geometry = new ScreenGeometry(() => null);
        SaveOneBenchRun();
        PushLine("/bench history");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.BenchRecentHeading, output, StringComparison.Ordinal);
        Assert.Contains(UsageText.BenchRunsSaved(1), output, StringComparison.Ordinal);
        Assert.DoesNotContain("1 run saved in ", output, StringComparison.Ordinal);   // not the markdown list
    }

    [Fact]
    public async Task WithGeometry_BenchHistory_FromThePage_ListsTheRuns_AndOpensNoPane()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _geometry = new ScreenGeometry(() => null);
        SaveOneBenchRun();
        var input = WebSteps(_ => SendFromPage("/bench history"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains("1 run saved in ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.BenchRecentHeading, output, StringComparison.Ordinal);
        Assert.Empty(_pcOnlyPanes);
    }

    [Fact]
    public void BenchHistory_OpensItsPaneOverAReply_ARunWaits()
    {
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Bench, "history"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.Bench, " HISTORY "));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Bench, "all"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Bench, ""));
    }

    /// <summary><c>/bench history</c> typed under a reply opens the Bench tab over it; one Esc closes it, and the reply ends whole.</summary>
    [Fact]
    public async Task MidTurn_BenchHistory_OpensTheBenchTabOverTheReply()
    {
        SaveOneBenchRun();
        MidTurnFixture(i =>
        {
            if (i == 1)
            {
                PushLine("/bench history");
            }
            else if (i == 2)
            {
                Scripted().Push(Keys.Escape);
            }
        });

        string output = await RunAsync();

        Assert.Contains(UsageText.BenchRecentHeading, output, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatScreen.MidTurnDeferredNotice("/bench"), output);
        Assert.Contains("One two three.", output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task UsageBench_WithoutThePane_PrintsTheInUseRows_TheGrid_AndTheRecentRuns()
    {
        _settings.Update(d => d.TtsOutput = false);
        SaveOneBenchRun();
        PushLine("/usage bench");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.BenchTestsHeading, output, StringComparison.Ordinal);
        Assert.Contains("llama" + BenchText.LevelSeparator + "none", output, StringComparison.Ordinal);
        Assert.Contains(UsageText.BenchRunsSaved(1), output, StringComparison.Ordinal);
    }
}
