using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>
/// How long the app waits on the inference backend. Resolved from the <em>effective</em> settings
/// (environment overrides already layered over the file by <c>EnvironmentOverrides.ApplyTo</c>),
/// so this type has one job: the interlock.
///
/// <para><b>The two values are an interlock, not independent knobs.</b> The per-request ceiling
/// must stay below the per-turn budget: <see cref="Assistant"/> checks its budget only at a
/// tool-loop boundary, so a request permitted to outlive the turn makes that check unreachable and
/// the turn runs until the HTTP layer gives up. <see cref="Resolve"/> enforces that here rather
/// than leaving it to whoever edits a variable.</para>
/// </summary>
/// <param name="Request">Ceiling on one HTTP request to the backend.</param>
/// <param name="Turn">Ceiling on a whole multi-iteration turn, checked at loop boundaries, never a CTS.</param>
public readonly record struct LlmTimeouts(TimeSpan Request, TimeSpan Turn)
{
    private const string Category = "Llm";

    /// <summary>The shipped per-request ceiling: an hour, since 2026-09-15 (75 s before) — the same as <see cref="MaxRequestSeconds"/>.</summary>
    public static readonly TimeSpan DefaultRequest = TimeSpan.FromSeconds(3600);

    /// <summary>The shipped per-turn budget: six hours, since 2026-09-15 (3600 s from 2026-09-14, 300 s before, 90 s at first) — the same as <see cref="MaxTurnSeconds"/>.</summary>
    public static readonly TimeSpan DefaultTurn = TimeSpan.FromSeconds(21600);

    /// <summary>
    /// The most <see cref="Request"/> may be set to: an hour. A bound that catches a millisecond
    /// value pasted in by mistake, not an opinion about how slow is too slow. <c>EnvironmentOverrides.ReadSeconds</c>
    /// and the settings menu apply the same two ceilings.
    /// </summary>
    public const double MaxRequestSeconds = 3600;

    /// <summary>The most <see cref="Turn"/> may be set to: six hours (the user's number, 2026-09-14).</summary>
    public const double MaxTurnSeconds = 21600;

    /// <summary>
    /// How much of the turn budget a single request may consume when the interlock has to
    /// adjust: five sixths, the ratio of the original 75/90 pair (the defaults are 3600 / 21600 s
    /// since 2026-09-15 and never trip it).
    /// </summary>
    private const double RequestShareOfTurn = 75.0 / 90.0;

    /// <summary>The compiled defaults.</summary>
    public static LlmTimeouts Default => new(DefaultRequest, DefaultTurn);

    /// <summary>
    /// The one resolver. Out-of-range saved values fall back to the default with a warning (a
    /// hand-edited file must not reach the transport); then the interlock is held.
    /// </summary>
    public static LlmTimeouts Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        var turn = Bound(effective.LlmTurnTimeoutSeconds, nameof(AppSettingsData.LlmTurnTimeoutSeconds), DefaultTurn, MaxTurnSeconds);
        var request = Bound(effective.LlmRequestTimeoutSeconds, nameof(AppSettingsData.LlmRequestTimeoutSeconds), DefaultRequest, MaxRequestSeconds);

        // Hold the interlock. Raising only the turn budget leaves requests capped at 75 s, which
        // is the more likely mistake — someone tries a slow model, raises the number that sounds
        // like "how long may this take", and still sees requests abandoned at 75 seconds.
        if (request >= turn)
        {
            var adjusted = TimeSpan.FromSeconds(Math.Max(1, Math.Floor(turn.TotalSeconds * RequestShareOfTurn)));

            DiagnosticLog.Warn(Category,
                $"Request timeout {Format(request)} is not below the turn budget {Format(turn)}; " +
                "a request that outlives the turn makes the loop-boundary check unreachable. " +
                $"Using {Format(adjusted)} instead. Raise {EnvironmentOverrides.TurnTimeoutVariable} to allow a longer request.");

            request = adjusted;
        }

        return new LlmTimeouts(request, turn);
    }

    private static TimeSpan Bound(double seconds, string name, TimeSpan fallback, double max)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > max)
        {
            DiagnosticLog.Warn(Category,
                $"{name}={seconds.ToString(CultureInfo.InvariantCulture)} is out of range (more than 0, at most {max.ToString(CultureInfo.InvariantCulture)} seconds). Using {Format(fallback)}.");
            return fallback;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Seconds with up to two decimals, invariant, e.g. <c>7.5s</c>.</summary>
    public static string Format(TimeSpan value) =>
        value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s";
}
