using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Bench;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/test</c> on the screen (2026-09-28): a run's lines, table and saved run, the conversation untouched; the listing, the history, the refusals, ESC, the completion.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Test_RunsTheTest_WritesItsLineAndTable_SavesTheRun_TheConversationUntouched()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(FakeChatClient.Text("Drawer."), FakeChatClient.Usage(90, 2));
        _chat.EnqueueText("Hello.");
        PushLine("/test mind");
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
        Assert.Contains(BenchText.Tally(run), output);
        var result = Assert.Single(run.Results);
        Assert.Equal(("mind", BenchVerdict.Pass, "Drawer.", 90L), (result.Test, result.Verdict, result.Answer, result.PromptTokens));
    }

    [Fact]
    public async Task Test_AFail_ShowsTheAnswer()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("AGREE");
        PushLine("/test sycophancy");
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
        PushLine("/test history");
        PushLine("/test");
        PushLine("/exit");

        string first = await RunAsync();
        Assert.Contains(BenchText.NoRuns, first);
        Assert.Contains("Theory of Mind (Reasoning)", first);
        Assert.Contains("Context Saturation (Long Context)", first);
        Assert.Contains("/test history lists the saved runs", first);

        new BenchHistory(_settings.ProfileDirectory).Append(new BenchRun
        {
            At = DateTimeOffset.UnixEpoch,
            Model = "llama",
            Results = [new BenchResult { Test = "grid", Verdict = BenchVerdict.Fail, Reason = "no" }],
        });
        PushLine("/test history");
        PushLine("/exit");

        string second = await RunAsync();
        Assert.Contains("✗grid", second);
        Assert.Contains("1 run saved in " + Path.Combine(_settings.ProfileDirectory, BenchHistory.FileName), second);
    }

    [Fact]
    public async Task Test_AnUnknownWord_IsTheUsage_AndNothingIsSent()
    {
        PushLine("/test everything");
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
        PushLine("/test reasoning");
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
        PushLine("/test needle");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(BenchText.ContextLine(new BenchContext(32768)), output);
        Assert.Contains("✓ needle", output);
        Assert.Equal(373, _chat.Requests[0][1].Text.Split('\n').Count(line => line.StartsWith("The server rack", StringComparison.Ordinal) || line == NeedleTest.Needle));
    }

    [Fact]
    public void Test_CompletesTheIds_TheGroups_AllAndHistory()
    {
        var items = ChatScreen.ArgumentItems("/test", "", Sources());
        Assert.Equal(["grid", "rule", "mind", "sycophancy", "json", "state", "needle", "multihop", "saturation", "reasoning", "structured", "long", "all", "history"], items.Select(i => i.Text));
        Assert.Equal(["multihop", "mind"], ChatScreen.ArgumentItems("/test", "m", Sources()).Select(i => i.Text).OrderByDescending(t => t.Length));
        Assert.Empty(ChatScreen.ArgumentItems("/test", "mind x", Sources()));
    }
}
