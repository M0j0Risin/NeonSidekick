using NeonSidekick.Settings;
using NeonSidekick.Timers;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The Timers tab of /tools: the tone (2026-10-18) ────────────────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// Plays a timer tone for the <c>Timer tone</c> picker's highlighted row (2026-10-18, the user's ask): the screen's
    /// <see cref="TimerTonePlayer"/> with <c>replace: true</c>, so the row the cursor stops on is the one heard. Null where there
    /// is none (no preview). It has its own device, so unlike the voice preview it plays while a reply runs.
    /// </summary>
    public Func<string, CancellationToken, Task>? PreviewTone { get; set; }

    /// <summary>
    /// The <c>Timer tone</c> picker (2026-10-18): <see cref="TimerTones.Names"/> with a hint each, the cursor on the saved one;
    /// moving the cursor plays the row's tone (<see cref="PreviewTone"/>, under the picker's own token, not awaited, its fault
    /// observed). Opening the picker, Enter and Esc play nothing (<c>MenuPane.PickAsync</c> fires the highlight only on a move);
    /// <c>silent</c> plays nothing. True when a pick changed it.
    /// </summary>
    /// <remarks><paramref name="field"/> (2026-10-18): the Ralph loop's stop tone over the same picker, its preview the same; the gap review's
    /// and the code audit's stop tones since 2026-10-21.</remarks>
    private async Task<bool> PickTimerToneAsync(AppSettingsData saved, CancellationToken cancellationToken, SettingsField field = SettingsField.TimerTone)
    {
        IReadOnlyList<string> values = TimerTones.Names;
        var rows = values.Select(n => CameraModeLabel(n, TimerTones.Describe(n))).ToList();
        var page = new MenuPage(Crumb(FieldName(field)), rows, PickKeys);
        string current = field switch
        {
            SettingsField.RalphLoopStopTone => saved.RalphLoopStopTone,
            SettingsField.PlanGapStopTone => saved.PlanGapStopTone,
            SettingsField.CodeAuditStopTone => saved.CodeAuditStopTone,
            _ => saved.TimerTone,
        };
        int at = Math.Max(0, values.ToList().IndexOf(TimerTones.Normalize(current)));
        Action<int>? highlighted = PreviewTone is { } preview
            ? row => _ = preview(values[row], cancellationToken).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default)
            : null;
        int? picked = await PickAsync(page, at, cancellationToken, highlighted).ConfigureAwait(false);
        if (picked is not { } index || index >= values.Count)
        {
            return Unchanged();
        }

        string value = values[index];
        Action<AppSettingsData> set = field switch
        {
            SettingsField.RalphLoopStopTone => d => d.RalphLoopStopTone = value,
            SettingsField.PlanGapStopTone => d => d.PlanGapStopTone = value,
            SettingsField.CodeAuditStopTone => d => d.CodeAuditStopTone = value,
            _ => d => d.TimerTone = value,
        };
        Apply(field, set);
        return true;
    }
}
