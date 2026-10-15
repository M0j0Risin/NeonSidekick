using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// A tool's time argument (2026-10-15, out of <c>DockerLogsTool.ParseSince</c> for the event-log tools, the plan's move): an age
/// before now — <c>30s</c>, <c>10m</c>, <c>2h</c>, <c>1d</c>, <c>1w</c>, combined as <c>1h30m</c> — or a moment in ISO 8601. A moment
/// with no offset is read in <c>zone</c>: Docker passes UTC (its <c>AssumeUniversal</c>, unchanged), the event-log tools the user's
/// own zone, where a time in a DST gap or overlap takes the offset <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> gives it. An age
/// too large to subtract is null, never an <see cref="OverflowException"/>. Pure.
/// </summary>
public static partial class TimeArgument
{
    /// <summary><paramref name="text"/> as a moment, an age counted back from <paramref name="now"/>; null for neither.</summary>
    public static DateTimeOffset? Parse(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        string trimmed = text.Trim();
        if (Age().Match(trimmed) is { Success: true } match)
        {
            double seconds = 0;
            foreach (Capture part in match.Groups["part"].Captures)
            {
                var piece = AgePart().Match(part.Value);
                if (!double.TryParse(piece.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    return null;
                }

                seconds += n * piece.Groups["u"].Value.ToLowerInvariant() switch
                {
                    "s" => 1,
                    "m" => 60,
                    "h" => 3600,
                    "d" => 86_400,
                    _ => 7 * 86_400,
                };
            }

            // Ticks before now: an age reaching past year 1 is no moment.
            double ticks = seconds * TimeSpan.TicksPerSecond;
            if (double.IsNaN(ticks) || double.IsInfinity(ticks) || ticks > now.UtcTicks - DateTimeOffset.MinValue.UtcTicks)
            {
                return null;
            }

            return now - TimeSpan.FromTicks((long)ticks);
        }

        if (HasOffset().IsMatch(trimmed))
        {
            return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stated) ? stated : null;
        }

        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<part>\d+(?:\.\d+)?\s*[smhdw])+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Age();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>[smhdw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgePart();

    [GeneratedRegex(@"(?:[zZ]|[+\-]\d{2}:?\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex HasOffset();
}
