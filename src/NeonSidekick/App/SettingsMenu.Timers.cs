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
        var tone = ToneSetting(field);
        int at = Math.Max(0, values.ToList().IndexOf(TimerTones.Normalize(tone.Get(saved))));
        Action<int>? highlighted = PreviewTone is { } preview
            ? row => _ = preview(values[row], cancellationToken).ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default)
            : null;
        int? picked = await PickAsync(page, at, cancellationToken, highlighted).ConfigureAwait(false);
        if (picked is not { } index || index >= values.Count)
        {
            return Unchanged();
        }

        string value = values[index];
        Apply(field, d => tone.Set(d, value));
        return true;
    }

    /// <summary>
    /// A tone setting's reading and its writing, one entry each (2026-10-22, a review: two parallel switches, and a fifth tone added to one alone would
    /// have read or written <c>Timer tone</c> through the other's default); <c>Timer tone</c> for any other field.
    /// </summary>
    private static (Func<AppSettingsData, string> Get, Action<AppSettingsData, string> Set) ToneSetting(SettingsField field) => field switch
    {
        SettingsField.RalphLoopStopTone => (d => d.RalphLoopStopTone, (d, v) => d.RalphLoopStopTone = v),
        SettingsField.PlanGapStopTone => (d => d.PlanGapStopTone, (d, v) => d.PlanGapStopTone = v),
        SettingsField.CodeAuditStopTone => (d => d.CodeAuditStopTone, (d, v) => d.CodeAuditStopTone = v),
        _ => (d => d.TimerTone, (d, v) => d.TimerTone = v),
    };
}
