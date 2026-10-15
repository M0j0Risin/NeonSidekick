using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The spinner as the Web UI's status row reads it (2026-10-15): <see cref="ScreenPane.BusyNow"/> follows a start, a rename, a nested spinner and
/// an end without ever taking the pane's lock, and <see cref="ScreenPane.BusyChanged"/> says so each time.
/// </summary>
public partial class ScreenPaneTests
{
    [Fact]
    public void BusyNow_FollowsTheSpinner_ItsStepClockRestartingOnARename()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("", 0);
        Assert.Null(pane.BusyNow());

        using (var busy = pane.BeginBusy("thinking"))
        {
            _time.Advance(TimeSpan.FromSeconds(3));
            var first = pane.BusyNow()!.Value;
            Assert.Equal(("thinking", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)), (first.Label, first.Elapsed, first.Step));

            busy.SetLabel("read_file");
            _time.Advance(TimeSpan.FromSeconds(2));
            var renamed = pane.BusyNow()!.Value;
            Assert.Equal(("read_file", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)), (renamed.Label, renamed.Elapsed, renamed.Step));
            Assert.Equal(first.Since, renamed.Since);
            Assert.NotEqual(first.StageSince, renamed.StageSince);

            using (pane.BeginBusy("listing models"))
            {
                var nested = pane.BusyNow()!.Value;
                Assert.Equal(("listing models", TimeSpan.FromSeconds(5)), (nested.Label, nested.Elapsed));   // the outer's count runs on
            }

            Assert.Equal("read_file", pane.BusyNow()!.Value.Label);
        }

        Assert.Null(pane.BusyNow());
    }

    [Fact]
    public void BusyChanged_FiresOnAStartARenameAndAnEnd_ButNotOnTheSameLabel()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("", 0);
        int changes = 0;
        pane.BusyChanged += () => changes++;

        using (var busy = pane.BeginBusy("thinking"))
        {
            Assert.Equal(1, changes);
            busy.SetLabel("thinking");
            Assert.Equal(1, changes);
            busy.SetLabel("writing");
            Assert.Equal(2, changes);
        }

        Assert.Equal(3, changes);
    }

    [Fact]
    public void BusyNow_AnswersAtOnce_WhileThePanesLockIsHeld()
    {
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("", 0);
        using var busy = pane.BeginBusy("thinking");

        BusyReading? read = null;
        lock (pane.GateForTests)
        {
            // Another thread, as the Web UI's request would be: it must not wait on the lock the console holds.
            var reader = new Thread(() => read = pane.BusyNow());
            reader.Start();
            Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal("thinking", read!.Value.Label);
    }

    [Fact]
    public void AFlash_ShowsOverABusyRow_AndTheSpinnerComesBack()
    {
        // 2026-10-15 (E3): a phone's action flashes on the hint row even while a turn's spinner holds it.
        _cursorTop = 100;
        using var pane = Pane();
        pane.Show();
        pane.ShowInput("", 0);
        using var busy = pane.BeginBusy("thinking");

        pane.Flash("📱 Kitchen light: toggle");

        Assert.Equal("📱 Kitchen light: toggle", pane.AlertHint);
        Assert.Contains("📱 Kitchen light: toggle", Output, StringComparison.Ordinal);
        _time.Advance(ScreenPane.FlashLasts + ScreenPane.Tick);
        Assert.Null(pane.AlertHint);
    }

    [Fact]
    public void BusyNow_IsNull_WithThePaneDisabled()
    {
        using var pane = Pane(geometry: false);
        using (pane.BeginBusy("thinking"))
        {
            Assert.Null(pane.BusyNow());
        }
    }
}
