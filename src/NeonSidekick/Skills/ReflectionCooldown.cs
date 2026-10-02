using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Skills;

/// <summary>
/// The <c>Reflection cooldown (minutes)</c> setting (2026-09-19, the user's call — the churn guard
/// beside the instruction's wording): an automatic reflection is skipped while the newest skill a
/// reflection wrote (the skill records' mark since 2026-10-02, the session store's <c>reflections</c>
/// table until then) is younger than this — every one under <c>Reflection cooldown mode</c>
/// <c>all-skills</c>, only one whose turns loaded that skill under <c>last-written-skill</c>
/// (<see cref="ReflectionCooldownMode"/>); 0 = no cooldown; a <c>/learn</c> never waits, nor do turns
/// whose load of that skill was followed by an error (later on 2026-10-02). Five minutes by default (30 for an hour on 2026-09-19). <see cref="Resolve"/> is the one place the saved number
/// is read: out of range warns and uses the default, never clamps.
/// </summary>
public static class ReflectionCooldown
{
    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const int Default = AppSettingsData.DefaultReflectionCooldownMinutes;

    public const int Min = AppSettingsData.MinReflectionCooldownMinutes;

    public const int Max = AppSettingsData.MaxReflectionCooldownMinutes;

    /// <summary>The cooldown for <paramref name="effective"/> (<see cref="TimeSpan.Zero"/> for off); an out-of-range saved value warns and uses <see cref="Default"/>.</summary>
    public static TimeSpan Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        int minutes = effective.ReflectionCooldownMinutes;
        if (minutes >= Min && minutes <= Max)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        DiagnosticLog.Warn(SkillCatalog.Category,
            $"{nameof(AppSettingsData.ReflectionCooldownMinutes)}={minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)} is not {Min.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {Max.ToString(System.Globalization.CultureInfo.InvariantCulture)}. Using {Default.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        return TimeSpan.FromMinutes(Default);
    }
}
