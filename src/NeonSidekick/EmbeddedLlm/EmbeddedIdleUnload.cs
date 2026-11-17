using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// The <c>Embedded idle unload (min)</c> setting (2026-11-16, plan lucid-roaming-lantern #44): how long the embedded model may sit idle
/// before the screen unloads it. <see cref="Resolve"/> is the one place the saved number is read, as <c>ReflectionCooldown.Resolve</c>:
/// out of range (a hand-edited <c>profile.json</c>) warns and reads as the default, off, never clamps.
/// </summary>
public static class EmbeddedIdleUnload
{
    private const string Category = "EmbeddedLlm";

    public const int Default = AppSettingsData.DefaultEmbeddedIdleUnloadMinutes;

    public const int Min = AppSettingsData.MinEmbeddedIdleUnloadMinutes;

    public const int Max = AppSettingsData.MaxEmbeddedIdleUnloadMinutes;

    // The last out-of-range value warned about.
    private static int _warned;

    /// <summary>The idle time for <paramref name="effective"/>; <see cref="TimeSpan.Zero"/> for off, and for a value out of range (warned).</summary>
    public static TimeSpan Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        int minutes = effective.EmbeddedIdleUnloadMinutes;
        if (minutes >= Min && minutes <= Max)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        // Read at every arming (2026-11-16, the review's #12): warned once per value, as EmbeddedHfDownloadType.Resolve does. The field starts
        // at 0, a value in range, so the first bad one always warns.
        if (Interlocked.Exchange(ref _warned, minutes) != minutes)
        {
            DiagnosticLog.Warn(Category, string.Create(CultureInfo.InvariantCulture,
                $"{nameof(AppSettingsData.EmbeddedIdleUnloadMinutes)}={minutes} is not {Min} to {Max}. Using {Default} (off)."));
        }

        return TimeSpan.FromMinutes(Default);
    }
}
