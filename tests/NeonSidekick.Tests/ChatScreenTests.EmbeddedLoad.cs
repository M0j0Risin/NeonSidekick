using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Perf;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The embedded model's load under the reply's own watch (2026-10-01, the user's asks): a double-click on the spinner and its
/// label — <c>🦙 starting …</c>, <c>🦙 loading …</c> — cancels it as Ctrl+C does, and the row, the toolbar and the chords live
/// under it as under a reply. The startup connect is the load here: the fake's <see cref="FakeEmbeddedLlm.StartGate"/> holds it.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The pane laid out for clicks: the input row at 100, the busy row at 102, the toolbar at 103; the strip empty, so the spinner's frame is column 0.</summary>
    private void LoadOnThePane()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
    }

    /// <summary>Waits until <paramref name="done"/> holds, ten seconds at most, or throws: a gate that never opens would hang the run.</summary>
    private static async Task UntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The load's watch never got there.");
            }

            await Task.Delay(5, cancellationToken);
        }
    }

    [Fact]
    public async Task ADoubleClickOnTheLoadSpinner_CancelsTheLoad_AsCtrlCDoes()
    {
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            input.PushClick(3, 102);   // "⠋ 🦙 starting Gemma 4 E2B 00:00" from column 0
            input.PushClick(3, 102);
            await Task.Delay(Timeout.Infinite, ct);
        };

        string output = await RunAsync(input);

        Assert.Contains("· " + ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.Null(_session.Assistant);
        Assert.Null(embedded.Running);
        Assert.DoesNotContain(Titled(UsageText.Label + "   Statistics "), output);   // the reply's /usage there is the load's cancel
    }

    [Fact]
    public async Task ASingleClickOnTheLoadSpinner_CancelsNothing()
    {
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            input.PushClick(3, 102);
            await UntilAsync(() => !input.IsAvailable, ct);
        };

        string output = await RunAsync(input);

        Assert.DoesNotContain(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.NotNull(_session.Assistant);
        Assert.NotNull(embedded.Running);
    }

    [Fact]
    public async Task UnderTheLoad_CtrlH_OpensTheHelpPane_AndTheModelLoadsAfter()
    {
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = WhenIdle(
            i => { i.Push(Keys.Escape); opened.TrySetResult(); },   // the help pane's read, the load still held
            i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            input.Push(Keys.CtrlH);
            await opened.Task.WaitAsync(ct);
        };

        string output = await RunAsync(input);

        Assert.Contains(Titled(InfoPane.Title + "   Commands (basic)    Commands (advanced)    Keys "), output);
        Assert.DoesNotContain(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.NotNull(_session.Assistant);
        Assert.NotNull(embedded.Running);
    }

    [Fact]
    public async Task UnderTheLoad_TheToolbarsRisingChart_TogglesTheBar_BeforeTheModelIsUp()
    {
        var embedded = OnTheEmbeddedE2b();
        _settings.Update(d => d.ToolbarItems = ["profile", "perf"]);
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        bool toggledUnderTheLoad = false;
        embedded.StartGate = async ct =>
        {
            input.PushClick(3, 103);   // 📈 on the toolbar: /perf, a quick act run while the load waits
            input.PushClick(3, 103);
            await UntilAsync(() => Output.Contains(PerfText.BarNotice("led"), StringComparison.Ordinal), ct);   // the default look, led since 2026-10-02
            toggledUnderTheLoad = true;
        };

        string output = await RunAsync(input);

        Assert.True(toggledUnderTheLoad);
        Assert.Equal(["cpu", "ram", "gpu", "vram"], _settings.Current.PerformanceBarItems);
        Assert.NotNull(_session.Assistant);
        Assert.NotNull(embedded.Running);
    }

    [Fact]
    public async Task UnderTheLoad_Clear_CancelsTheLoad_AndRunsAtTheIdleLine()
    {
        // The reply's rule (the user's pick): /clear cancels what runs, then runs.
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            PushLine(input, "/clear");
            await Task.Delay(Timeout.Infinite, ct);
        };

        string output = await RunAsync(input);

        int cancelled = output.IndexOf(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), StringComparison.Ordinal);
        Assert.True(cancelled > 0, output);
        Assert.True(output.IndexOf(ScreenMarker, cancelled, StringComparison.Ordinal) > cancelled);   // the wipe after it
        Assert.Null(_session.Assistant);
        Assert.Null(embedded.Running);
    }

    [Fact]
    public void LoadNotices_ArePinned()
    {
        Assert.Equal("(/profile runs when the load ends)", ChatScreen.MidTurnDeferredNotice("/profile", load: true));
        Assert.Equal(ChatScreen.MidTurnDeferredNotice("/profile"), ChatScreen.MidTurnDeferredNotice("/profile", load: false));
        Assert.Equal("(" + NoticeGlyphs.Tts + "speech output on — connecting when the load ends)", ChatScreen.MidTurnSwitchNotice(ChatScreen.SpeechOutputWord, on: true, load: true));
        Assert.Equal("(" + NoticeGlyphs.Tts + "speech output off — applies when the load ends)", ChatScreen.MidTurnSwitchNotice(ChatScreen.SpeechOutputWord, on: false, load: true));
        Assert.Equal(ChatScreen.MidTurnSwitchNotice(ChatScreen.SpeechOutputWord, true), ChatScreen.MidTurnSwitchNotice(ChatScreen.SpeechOutputWord, true, load: false));
        Assert.Equal("(applies when the model has loaded)", ChatScreen.LoadAppliesNotice);
    }

    [Fact]
    public void UnderTheLoad_SamplingWaits_EveryOtherCommandKeepsItsReplyClass()
    {
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicyUnderLoad(SlashCommand.Sampling, ""));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicyUnderLoad(SlashCommand.Sampling, "temperature 0.2"));
        foreach (var command in Enum.GetValues<SlashCommand>().Where(c => c != SlashCommand.Sampling))
        {
            foreach (string args in (string[])["", "x"])
            {
                Assert.Equal(ChatScreen.MidTurnPolicy(command, args), ChatScreen.MidTurnPolicyUnderLoad(command, args));
            }
        }
    }

    [Fact]
    public async Task ADoubleClickOnTheTallyBesideTheLoadSpinner_OpensUsage_AndTheLoadGoesOn()
    {
        // The review's finding (2026-10-01): the whole usage zone was the load's cancel, the tally included.
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        _session.Usage.Add(new TokenUsage(1200, 30, 1230, 1, TimeSpan.Zero, TimeSpan.Zero));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = WhenIdle(
            i => { i.Push(Keys.Escape); opened.TrySetResult(); },   // the Usage pane's read, the load still held
            i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            // "⠋ 🦙 starting Gemma 4 E2B 00:00 · 1.2k tokens": the label's zone ends at 30, the tally starts at 34.
            input.PushClick(36, 102);
            input.PushClick(36, 102);
            await opened.Task.WaitAsync(ct);
        };

        string output = await RunAsync(input);

        Assert.Contains(Titled(UsageText.Label + "   Statistics "), output);
        Assert.DoesNotContain(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.NotNull(embedded.Running);
    }

    [Fact]
    public async Task UnderTheLoad_AReasoningLevel_ThenCtrlC_CancelsTheLoad_AndStartsNoOther()
    {
        // The review's finding: the reconnect the level owed ran at the watch's end and started the load just cancelled.
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            if (embedded.Starts.Count > 1)
            {
                return;   // a second start: the bug, asserted below rather than hung on
            }

            PushLine(input, "/reasoning high");
            await UntilAsync(() => Output.Contains(ChatScreen.LoadAppliesNotice, StringComparison.Ordinal), ct);
            input.Push(Keys.CtrlC);
            await Task.Delay(Timeout.Infinite, ct);
        };

        string output = await RunAsync(input);

        Assert.Single(embedded.Starts);
        Assert.Equal("high", _settings.Current.LlmReasoning);
        Assert.Contains(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
        Assert.Null(_session.Assistant);
    }

    [Fact]
    public async Task UnderTheLoad_AReasoningLevel_ReconnectsOnceTheModelHasLoaded()
    {
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            if (embedded.Starts.Count == 1)
            {
                PushLine(input, "/reasoning high");
                await UntilAsync(() => Output.Contains(ChatScreen.LoadAppliesNotice, StringComparison.Ordinal), ct);
            }
        };

        string output = await RunAsync(input);

        Assert.Equal(2, embedded.Starts.Count);
        Assert.Equal("high", embedded.StartSettings[^1].LlmReasoning);
        Assert.Contains(ChatScreen.LoadAppliesNotice, output);
        Assert.NotNull(_session.Assistant);
    }

    [Fact]
    public async Task UnderTheLoad_ACancel_FollowsQueueCancelMode_TheQueuedMessageIsDropped()
    {
        // The review's finding: the message queued for a model that never came was sent to none, and lost.
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            PushLine(input, "hello there");
            await UntilAsync(() => !input.IsAvailable, ct);
            await Task.Delay(60, ct);   // the watcher queues the line
            input.Push(Keys.CtrlC);
            await Task.Delay(Timeout.Infinite, ct);
        };

        string output = await RunAsync(input);

        Assert.Contains(ChatScreen.QueueDroppedNotice(1), output);
        Assert.DoesNotContain(ChatScreen.NoAssistantError, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task UnderTheLoad_ASwitch_SaysTheLoad_AndItsLineComesBeforeTheCancelledNotice()
    {
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        string notice = ChatScreen.MidTurnSwitchNotice(ChatScreen.VoiceInputWord, on: false, load: true);
        _settings.Update(d => d.SttInput = true);
        embedded.StartGate = async ct =>
        {
            PushLine(input, "/stt off");
            await UntilAsync(() => Output.Contains(notice, StringComparison.Ordinal), ct);
            input.Push(Keys.CtrlC);
            await Task.Delay(Timeout.Infinite, ct);
        };

        string output = await RunAsync(input);

        int said = output.IndexOf(notice, StringComparison.Ordinal);
        int cancelled = output.IndexOf(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), StringComparison.Ordinal);
        Assert.True(said > 0 && cancelled > said, output);
        Assert.False(_settings.Current.SttInput);
    }

    [Fact]
    public async Task UnderTheLoad_Sampling_WaitsForTheLoad_AndEditsTheLoadedModel()
    {
        // The review's finding: run at once, the edit went to the endpoint being swapped out.
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        var input = WhenIdle(i => PushLine(i, "/exit"));
        string notice = ChatScreen.MidTurnDeferredNotice("/sampling", load: true);
        embedded.StartGate = async ct =>
        {
            PushLine(input, "/sampling temperature 0.2");
            await UntilAsync(() => Output.Contains(notice, StringComparison.Ordinal), ct);
        };

        string output = await RunAsync(input);

        Assert.Contains(notice, output);
        var entry = Assert.Single(_settings.Current.LlmSampling!);
        Assert.Equal(_session.Endpoint!.ModelId, entry.Key);
        Assert.Equal(0.2, entry.Value.Temperature);
    }

    [Fact]
    public async Task UnderTheLoad_Esc_IsSpent_TheDraftOnTheRowSurvivesIt()
    {
        // The review's finding: ESC is no cancel under the load, and kept as type-ahead it fired at the idle line after it
        // (here it would have cleared the draft, so the Enter sent nothing).
        var embedded = OnTheEmbeddedE2b();
        LoadOnThePane();
        _chat.EnqueueText("Hi.");
        var input = WhenIdle(i => i.Push(Keys.Enter), i => PushLine(i, "/exit"));
        embedded.StartGate = async ct =>
        {
            input.Push(Keys.Char('a'), Keys.Char('b'), Keys.Char('c'), Keys.Escape);
            await UntilAsync(() => !input.IsAvailable, ct);
            await Task.Delay(60, ct);   // the watcher takes the keys
        };

        string output = await RunAsync(input);

        Assert.NotNull(embedded.Running);
        Assert.Single(_chat.Requests);
        Assert.DoesNotContain(ChatScreen.ConnectCancelledNotice(NoticeGlyphs.Llm), output);
    }
}
