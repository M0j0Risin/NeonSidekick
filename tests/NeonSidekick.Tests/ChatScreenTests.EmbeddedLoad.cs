using NeonSidekick.App;
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
            await UntilAsync(() => Output.Contains(PerfText.BarNotice("text"), StringComparison.Ordinal), ct);
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
}
