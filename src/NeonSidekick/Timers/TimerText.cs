using System.Globalization;
using System.Text;
using NeonSidekick.Llm.Tools;
using NeonSidekick.UI;

namespace NeonSidekick.Timers;

/// <summary>
/// The words the timers speak and the forms they accept: pure statics, every string pinned.
/// The tool results are sentences the model reads and, verbatim, the one dim <c>🛠️</c> line the
/// transcript shows; the alert wording is what the screen prints and what it speaks. Invariant
/// culture throughout.
/// </summary>
public static class TimerText
{
    public const int MaxNameLength = 40;

    /// <summary>Reserved: <c>/timer stop all</c>.</summary>
    public const string AllName = "all";

    public const string NoTimers = "no timers are running";
    public const string NoDuration = "Error: give the duration in hours, minutes and/or seconds";
    public const string BadDuration = "Error: a timer runs from 1 second to 24 hours";
    public static readonly string BadName = "Error: a timer name is 1 to " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters and not '" + AllName + "'";
    public const string NoName = "Error: give the name of the timer to stop";
    public const string SilenceHint = "press Enter to silence";

    public static string Full(int max) =>
        "Error: too many timers (" + max.ToString(CultureInfo.InvariantCulture) + "); stop one first";

    // ---- names ----

    /// <summary>Trimmed, inner whitespace collapsed to one space; the casing is kept for display.</summary>
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        bool pendingSpace = false;
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>A normalised name the board accepts: 1 to <see cref="MaxNameLength"/> characters, not <see cref="AllName"/>.</summary>
    public static bool IsValidName(string normalized)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        return normalized.Length is > 0 and <= MaxNameLength && !NameEquals(normalized, AllName);
    }

    public static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- durations ----

    /// <summary>
    /// <c>10m</c>, <c>90s</c>, <c>1h30m</c>, <c>1h 30m</c>, <c>2 hours</c>, <c>5 min</c>; a bare
    /// number is minutes. Each unit at most once, in any order; nothing else, and never zero.
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan duration) => TryParseDuration(text, withDays: false, out duration);

    /// <summary>
    /// <see cref="TryParseDuration(string, out TimeSpan)"/> with a day unit (<c>2d</c>, <c>1 day</c>,
    /// <c>1d 6h</c>) when <paramref name="withDays"/> is set: the form <c>/sessions purge older</c>
    /// takes (2026-09-21), kept off for <c>/timer</c>, whose ceiling is 24 hours and whose bare
    /// number is still minutes.
    /// </summary>
    public static bool TryParseDuration(string text, bool withDays, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (text is null)
        {
            return false;
        }

        string s = text.Trim();
        if (s.Length == 0)
        {
            return false;
        }

        if (s.All(char.IsAsciiDigit))
        {
            if (s.Length > 6)
            {
                return false;
            }

            duration = TimeSpan.FromMinutes(int.Parse(s, CultureInfo.InvariantCulture));
            return duration > TimeSpan.Zero;
        }

        long seconds = 0;
        bool sawDays = false, sawHours = false, sawMinutes = false, sawSeconds = false;
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            int digitsStart = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
            {
                i++;
            }

            int digits = i - digitsStart;
            if (digits is 0 or > 6)
            {
                return false;
            }

            int amount = int.Parse(s.AsSpan(digitsStart, digits), CultureInfo.InvariantCulture);
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            int unitStart = i;
            while (i < s.Length && char.IsAsciiLetter(s[i]))
            {
                i++;
            }

            switch (s[unitStart..i].ToLowerInvariant())
            {
                case "d" or "day" or "days" when withDays:
                    if (sawDays)
                    {
                        return false;
                    }

                    sawDays = true;
                    seconds += amount * 86400L;
                    break;
                case "h" or "hr" or "hrs" or "hour" or "hours":
                    if (sawHours)
                    {
                        return false;
                    }

                    sawHours = true;
                    seconds += amount * 3600L;
                    break;
                case "m" or "min" or "mins" or "minute" or "minutes":
                    if (sawMinutes)
                    {
                        return false;
                    }

                    sawMinutes = true;
                    seconds += amount * 60L;
                    break;
                case "s" or "sec" or "secs" or "second" or "seconds":
                    if (sawSeconds)
                    {
                        return false;
                    }

                    sawSeconds = true;
                    seconds += amount;
                    break;
                default:
                    return false;
            }
        }

        if (seconds <= 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary><c>10 minutes</c>, <c>1 hour 30 minutes</c>, <c>1 minute 30 seconds</c>, <c>0 seconds</c>; whole seconds.</summary>
    public static string Describe(TimeSpan duration) => Parts(duration, plural: true);

    /// <summary>The name an unnamed timer gets: <see cref="Describe"/> without the plural, <c>10 minute</c>.</summary>
    public static string DefaultName(TimeSpan duration) => Parts(duration, plural: false);

    /// <summary>
    /// The countdown form for the hint row: <c>09:27</c>, <c>00:45</c>, <c>03:10:15</c> once an
    /// hour is left — <see cref="ElapsedText.Countdown"/>, the shape a spinner's elapsed time shares.
    /// </summary>
    public static string Countdown(TimeSpan duration) => ElapsedText.Countdown(duration);

    /// <summary><c>14:15</c>.</summary>
    public static string Clock(DateTimeOffset moment) => moment.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Parts(TimeSpan duration, bool plural)
    {
        long total = (long)Math.Round(duration.TotalSeconds);
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        var parts = new List<string>(3);
        if (h > 0)
        {
            parts.Add(Unit(h, "hour", plural));
        }

        if (m > 0)
        {
            parts.Add(Unit(m, "minute", plural));
        }

        if (s > 0 || parts.Count == 0)
        {
            parts.Add(Unit(s, "second", plural));
        }

        return string.Join(' ', parts);
    }

    private static string Unit(long n, string unit, bool plural) =>
        plural ? ClockText.Count((int)n, unit) : n.ToString(CultureInfo.InvariantCulture) + " " + unit;

    // ---- tool sentences ----

    /// <summary><c>started the cooking timer: 10 minutes, done at 14:15</c>.</summary>
    public static string Started(TimerSnapshot timer) =>
        $"started the {timer.Name} timer: {Describe(timer.Duration)}, done at {Clock(timer.DueLocal)}";

    /// <summary>The name is taken: <c>… is already running (7 minutes 12 seconds left) …</c> or <c>… is done and ringing …</c>.</summary>
    public static string Duplicate(TimerSnapshot existing) =>
        existing.Ringing
            ? $"Error: a timer called '{existing.Name}' is done and ringing; stop it first or use another name"
            : $"Error: a timer called '{existing.Name}' is already running ({Describe(existing.Remaining)} left); stop it first or use another name";

    /// <summary><c>stopped the cooking timer with 7 minutes 12 seconds left</c>, or <c>silenced the cooking timer</c> when it was ringing.</summary>
    public static string Stopped(TimerSnapshot timer) =>
        timer.Ringing
            ? $"silenced the {timer.Name} timer"
            : $"stopped the {timer.Name} timer with {Describe(timer.Remaining)} left";

    /// <summary><c>Error: no timer called 'cooking'; running: tea, eggs</c>, or <c>…; no timers are running</c>.</summary>
    public static string NoSuchTimer(string name, IReadOnlyList<TimerSnapshot> timers)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(timers);
        string tail = timers.Count == 0 ? NoTimers : "running: " + string.Join(", ", timers.Select(t => t.Name));
        return $"Error: no timer called '{name}'; {tail}";
    }

    /// <summary>One timer: <c>cooking: 7 minutes 12 seconds left of 10 minutes (done at 14:15)</c> or <c>tea: done, ringing</c>.</summary>
    public static string Line(TimerSnapshot timer) =>
        timer.Ringing
            ? $"{timer.Name}: done, ringing"
            : $"{timer.Name}: {Describe(timer.Remaining)} left of {Describe(timer.Duration)} (done at {Clock(timer.DueLocal)})";

    /// <summary>Every <see cref="Line"/> joined with <c>; </c>, or <see cref="NoTimers"/>.</summary>
    public static string List(IReadOnlyList<TimerSnapshot> timers)
    {
        ArgumentNullException.ThrowIfNull(timers);
        return timers.Count == 0 ? NoTimers : string.Join("; ", timers.Select(Line));
    }

    // ---- alerts ----

    /// <summary><c>cooking timer done (10 minutes) — press Enter to silence</c>; a repeat: <c>cooking timer still ringing (2 minutes) — press Enter to silence</c>.</summary>
    public static string AlertLine(TimerAlert alert) =>
        alert.Repeat == 0
            ? $"{alert.Name} timer done ({Describe(alert.Duration)}) — {SilenceHint}"
            : $"{alert.Name} timer still ringing ({Describe(alert.Overdue)}) — {SilenceHint}";

    /// <summary><c>The cooking timer is done.</c>; a repeat: <c>The cooking timer is still ringing.</c></summary>
    public static string AlertSpeech(TimerAlert alert) =>
        alert.Repeat == 0 ? $"The {alert.Name} timer is done." : $"The {alert.Name} timer is still ringing.";

    // ---- screen lines ----

    /// <summary>The hint row's timer glyph (U+23F0, two cells in the terminal — <see cref="UI.TextCells"/> counts it so).</summary>
    public const string StatusGlyph = "⏰";

    /// <summary>The most cells a name takes on the hint row; a longer one ends in an ellipsis (<see cref="ScreenPane.Fit"/>).</summary>
    public const int StatusNameCells = 10;

    /// <summary><c>⏰ cooking 07:12 · tea ringing</c>, a long name cut to <see cref="StatusNameCells"/> (<c>the big p… 07:12</c>); null when there are none.</summary>
    public static string? StatusLine(IReadOnlyList<TimerSnapshot> timers)
    {
        ArgumentNullException.ThrowIfNull(timers);
        if (timers.Count == 0)
        {
            return null;
        }

        return StatusGlyph + " " + string.Join(" · ", timers.Select(t => StatusName(t.Name) + (t.Ringing ? " ringing" : " " + Countdown(t.Remaining))));
    }

    /// <summary>The name as the hint row shows it: whole up to <see cref="StatusNameCells"/> cells, else cut with an ellipsis inside them.</summary>
    public static string StatusName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ScreenPane.Fit(name, StatusNameCells);
    }

    /// <summary>The question a double-click on the hint row's timers asks before stopping them all (2026-10-13, the user's words). Pinned.</summary>
    public const string CancelAllPrompt = "Cancel all timers?";

    /// <summary><c>(stopped 2 timers)</c>, <c>(stopped 1 timer)</c>, <c>(no timers)</c>.</summary>
    public static string StoppedAll(int count) => count == 0 ? "(" + StatusGlyph + " no timers)" : "(" + StatusGlyph + " stopped " + ClockText.Count(count, "timer") + ")";   // the clock since 2026-09-22
}
