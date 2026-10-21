using NeonSidekick.App;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The stop tones (2026-10-21, the user's ask): the gap review's and the code audit's rows, last in their sections, over the Timer tone
/// picker; all three silent by default (opt in). The pass rows' ceiling went to 100 the same day.
/// </summary>
public partial class SettingsMenuTests
{
    [Fact]
    public void TheStopTones_AreSilentByDefault_LastInTheirSections()
    {
        var data = new AppSettingsData();
        Assert.Equal((TimerTones.Silent, TimerTones.Silent, TimerTones.Silent), (data.PlanGapStopTone, data.CodeAuditStopTone, data.RalphLoopStopTone));
        Assert.Equal("Plan gap stop tone", SettingsMenu.FieldName(SettingsField.PlanGapStopTone));
        Assert.Equal("Code audit stop tone", SettingsMenu.FieldName(SettingsField.CodeAuditStopTone));
        Assert.Equal("silent", SettingsMenu.FieldValue(SettingsField.PlanGapStopTone, data, _settings.ProfileDirectory));
        Assert.Equal("chime", SettingsMenu.FieldValue(SettingsField.CodeAuditStopTone, new AppSettingsData { CodeAuditStopTone = "Chime" }, _settings.ProfileDirectory));
        Assert.Equal("silent", SettingsMenu.FieldValue(SettingsField.CodeAuditStopTone, new AppSettingsData { CodeAuditStopTone = "gong" }, _settings.ProfileDirectory));   // unknown reads as silent
        Assert.False(SettingsMenu.IsLlmField(SettingsField.PlanGapStopTone));
        Assert.False(SettingsMenu.IsLlmField(SettingsField.CodeAuditStopTone));
        var automation = SettingsMenu.TabFields[(int)SettingsTab.Automation].ToList();   // Servers › Common until 2026-10-21
        Assert.Equal(automation.IndexOf(SettingsField.PlanGapAskUser) + 1, automation.IndexOf(SettingsField.PlanGapStopTone));
        Assert.Equal(automation.IndexOf(SettingsField.PlanGapStopTone) + 1, automation.IndexOf(SettingsField.RalphLoopReviewer));
        Assert.Equal(automation.IndexOf(SettingsField.CodeAuditPassToolCalls) + 1, automation.IndexOf(SettingsField.CodeAuditStopTone));
        // The pass rows read as caps.
        Assert.Equal(
            ["Plan gap max passes", "Code audit max passes", "Ralph loop max passes", "Ralph loop max step passes"],
            new[] { SettingsField.PlanGapPasses, SettingsField.CodeAuditPasses, SettingsField.RalphLoopPasses, SettingsField.RalphLoopPassesPerStep }.Select(SettingsMenu.FieldName));
    }

    [Theory]
    [InlineData(SettingsField.PlanGapStopTone)]
    [InlineData(SettingsField.CodeAuditStopTone)]
    [InlineData(SettingsField.RalphLoopStopTone)]
    public async Task TheStopTonePicker_SavesThePick(SettingsField field)
    {
        Down((int)field);
        Push(Keys.Enter, Keys.Down, Keys.Enter, Keys.Escape);   // the tones: silent under the cursor, chime picked

        await _menu.ShowAsync(CancellationToken.None);

        var saved = _settings.Current;
        Assert.Equal(TimerTones.Chime, field switch
        {
            SettingsField.PlanGapStopTone => saved.PlanGapStopTone,
            SettingsField.CodeAuditStopTone => saved.CodeAuditStopTone,
            _ => saved.RalphLoopStopTone,
        });
        Assert.Equal(new AppSettingsData().TimerTone, saved.TimerTone);   // the timer's own tone untouched
    }

    [Theory]
    [InlineData(SettingsField.PlanGapPasses)]
    [InlineData(SettingsField.CodeAuditPasses)]
    public async Task ThePassRows_Take100(SettingsField field)
    {
        Down((int)field);
        Push(Keys.Enter);
        Backspace(5);
        Push([.. "100".Select(Keys.Char), Keys.Enter, Keys.Escape]);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Equal(100, field == SettingsField.PlanGapPasses ? _settings.Current.PlanGapPasses : _settings.Current.CodeAuditPasses);
    }

    [Fact]
    public async Task ThePlanGapPassesRow_Refuses101()
    {
        Down((int)SettingsField.PlanGapPasses);
        Push(Keys.Enter);
        Backspace(5);
        Push([.. "101".Select(Keys.Char), Keys.Enter, Keys.Escape]);

        await _menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Plan gap max passes " + SettingsMenu.PlanGapPassesRangeError, Output);
        Assert.Equal(PreFlipDefaults.Data().PlanGapPasses, _settings.Current.PlanGapPasses);   // unchanged: the fixture's 5 (PreFlipDefaults; the default is 50 since 2026-10-21)
        Assert.Equal("must be 0 to 100 passes (0 for no limit)", SettingsMenu.PlanGapPassesRangeError);
    }

    /// <summary>Every stop the user didn't cause rings; a message never, Esc never, a failed pass (or one never sent, or a moved folder) does.</summary>
    [Theory]
    [InlineData(PlanGapStop.Converged, false, true)]
    [InlineData(PlanGapStop.Limit, false, true)]
    [InlineData(PlanGapStop.Stalled, false, true)]
    [InlineData(PlanGapStop.NoRevision, false, true)]
    [InlineData(PlanGapStop.Unconfirmed, false, true)]
    [InlineData(PlanGapStop.UserSpoke, false, false)]
    [InlineData(PlanGapStop.UserSpoke, true, false)]
    [InlineData(PlanGapStop.Stopped, false, false)]
    [InlineData(PlanGapStop.Stopped, true, true)]
    public void PlanGapLoop_Rings_ForEveryStopTheUserDidNotCause(PlanGapStop reason, bool failed, bool rings) =>
        Assert.Equal(rings, PlanGapLoop.Rings(reason, failed));

    [Fact]
    public void PlanGapLoop_Rings_CoversEveryStop() =>
        Assert.Equal(7, Enum.GetValues<PlanGapStop>().Length);   // a new stop decides its ring here and above
}
