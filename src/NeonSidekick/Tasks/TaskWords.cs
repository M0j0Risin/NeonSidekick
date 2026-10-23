using System.Globalization;
using System.Text;
using System.Xml;

namespace NeonSidekick.Tasks;

/// <summary>
/// A task's triggers and last results in words (2026-10-15, the admin-tools plan): <c>every day at 03:00</c>, <c>at startup after 5 minutes</c>,
/// <c>0x41301 is running</c>. The trigger's own times are the XML's ISO stamps as written (the computer's own clock). Pure.
/// </summary>
public static class TaskWords
{
    /// <summary>
    /// A DATE as a time, null for never (the zero DATE, or any date before 2000: the Task Scheduler's own "never" is 1999-11-30) or for a value
    /// outside DATE's range (2026-10-23, the admin review's A9: FromOADate threw ArgumentException for one, which no caller caught, failing a
    /// whole task_list).
    /// </summary>
    public static DateTime? Date(double value)
    {
        if (double.IsNaN(value) || value <= 1.0 || value >= 2958466.0)
        {
            return null;
        }

        var date = DateTime.FromOADate(value);
        return date.Year < 2000 ? null : date;
    }

    /// <summary>One trigger in words.</summary>
    public static string Trigger(TaskTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var v = trigger.Values;
        string at = Get(v, "StartBoundary") is { } start ? " at " + Clock(start) : "";
        string words = trigger.Kind switch
        {
            "TimeTrigger" => "once" + (Get(v, "StartBoundary") is { } once ? " at " + Stamp(once) : ""),
            "CalendarTrigger" => Get(v, "Schedule") switch
            {
                "ScheduleByDay" => (Get(v, "DaysInterval") is { } days && days != "1" ? "every " + days + " days" : "every day") + at,
                "ScheduleByWeek" => (Get(v, "WeeksInterval") is { } weeks && weeks != "1" ? "every " + weeks + " weeks" : "every week") + (Get(v, "DaysOfWeek") is { Length: > 0 } dow ? " on " + List(dow) : "") + at,
                "ScheduleByMonth" => "every month" + (Get(v, "DaysOfMonth") is { Length: > 0 } dom ? " on day " + List(dom) : "") + In(v) + at,
                "ScheduleByMonthDayOfWeek" => "every month on the " + Weeks(Get(v, "Weeks") ?? Get(v, "Week") ?? "") + " " + List(Get(v, "DaysOfWeek") ?? "") + In(v) + at,
                _ => "on a schedule" + at,
            },
            "BootTrigger" => "at startup",
            "LogonTrigger" => "at sign-in" + (Get(v, "UserId") is { } user ? " of " + user : ""),
            "IdleTrigger" => "when the computer is idle",
            "EventTrigger" => "on an event",
            "RegistrationTrigger" => "when the task is created or changed",
            "SessionStateChangeTrigger" => "on " + (Get(v, "StateChange") switch
            {
                "ConsoleConnect" => "a local connection",
                "ConsoleDisconnect" => "a local disconnection",
                "RemoteConnect" => "a remote connection",
                "RemoteDisconnect" => "a remote disconnection",
                "SessionLock" => "lock",
                "SessionUnlock" => "unlock",
                { } other => other,
                null => "a session change",
            }),
            _ => trigger.Kind,
        };
        var text = new StringBuilder(words);
        if (trigger.Kind == "CalendarTrigger" && Get(v, "StartBoundary") is { Length: >= 10 } from)
        {
            // The day it counts from (2026-10-23, the admin review's S8): "every 3 days" says nothing without it, and a trigger that has not
            // started yet read as one already running.
            text.Append(" from ").Append(from[..10]);
        }

        if (Get(v, "EndBoundary") is { } end)
        {
            // A trigger past its end read as live (S8).
            text.Append(" until ").Append(Stamp(end));
        }

        if (Get(v, "Delay") is { } delay && !IsZero(delay))
        {
            text.Append(" after ").Append(Duration(delay));
        }

        if (Get(v, "Interval") is { } interval)
        {
            text.Append(", repeating every ").Append(Duration(interval));
            if (Get(v, "Duration") is { } duration)
            {
                text.Append(" for ").Append(Duration(duration));
            }
        }

        if (!trigger.Enabled)
        {
            text.Append(" (disabled)");
        }

        return text.ToString();
    }

    /// <summary>A last result in words: the known codes named, an exit code otherwise.</summary>
    public static string Result(int code) => unchecked((uint)code) switch
    {
        0 => "succeeded",
        0x41300 => "ready to run",
        0x41301 => "is running",
        0x41302 => "is disabled",
        0x41303 => "has not run yet",
        0x41304 => "has no more runs scheduled",
        0x41305 => "is missing properties",
        0x41306 => "was ended by a user",
        0x41325 => "was queued",
        0x8004130F => "could not run: its account's credentials are not set or were refused",
        0x80041314 => "could not run: its account settings and run options do not go together",   // SCHED_E_UNSUPPORTED_ACCOUNT_OPTION (2026-10-23, the admin review's G6: it was called a time limit)
        0x8004131E => "did not run: past its end boundary",
        0x80041324 => "has already tried to run",
        0x8004131F => "did not start: an instance is already running",
        0x80070002 => "failed: a file it needs was not found",
        0x80070005 => "failed: access denied",
        0x800710E0 => "was refused: the operator or the account's sign-in refused the run",
        0xC000013A => "was ended (the program was terminated)",
        _ => "exited with " + code.ToString(CultureInfo.InvariantCulture) + " (0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) + ")",
    };

    /// <summary>An ISO 8601 duration (<c>PT5M</c>, <c>P1D</c>) in words; the text itself when it is not one.</summary>
    public static string Duration(string iso)
    {
        try
        {
            var span = XmlConvert.ToTimeSpan(iso);
            if (span.TotalDays >= 1 && span.TotalDays == Math.Floor(span.TotalDays))
            {
                return Count((int)span.TotalDays, "day");
            }

            if (span.TotalHours >= 1 && span.TotalHours == Math.Floor(span.TotalHours))
            {
                return Count((int)span.TotalHours, "hour");
            }

            if (span.TotalMinutes >= 1 && span.TotalMinutes == Math.Floor(span.TotalMinutes))
            {
                return Count((int)span.TotalMinutes, "minute");
            }

            if (span.TotalSeconds == Math.Floor(span.TotalSeconds))
            {
                return Count((int)span.TotalSeconds, "second");
            }

            // A part of a second (2026-10-23, the admin review's S17: PT0.5S read "0 seconds").
            return span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // OverflowException too (2026-10-23, the admin review's B10): XmlConvert.ToTimeSpan throws it for P99999999D, and it escaped
            // task_info and /tasks.
            return iso;
        }
    }

    private static bool IsZero(string iso)
    {
        try
        {
            return XmlConvert.ToTimeSpan(iso) == TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>The months of a monthly trigger, unless it is every month.</summary>
    private static string In(IReadOnlyDictionary<string, string> v) =>
        Get(v, "Months") is { Length: > 0 } months && months.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 12 ? " in " + List(months) : "";

    /// <summary>A monthly day-of-week trigger's weeks (<c>1</c>…<c>4</c>, <c>Last</c>) as words: <c>first, third</c>.</summary>
    private static string Weeks(string weeks) => string.Join(", ", weeks.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w switch
    {
        "1" => "first",
        "2" => "second",
        "3" => "third",
        "4" => "fourth",
        _ when w.Equals("Last", StringComparison.OrdinalIgnoreCase) => "last",
        _ => w,
    }));

    /// <summary>An ISO stamp as the computer wrote it, the T made a space and the seconds dropped when zero.</summary>
    public static string Stamp(string iso)
    {
        string text = iso.Replace('T', ' ');
        return text.Length >= 19 && text.EndsWith(":00", StringComparison.Ordinal) && text.Length == 19 ? text[..16] : text;
    }

    /// <summary>
    /// A start's time of day. A stamp with a zone (<c>Z</c>, <c>+02:00</c>; "synchronize across time zones") keeps it (2026-10-23, the admin
    /// review's G8: <c>03:00Z</c> read as the computer's own 03:00); one without is the computer's own clock, as written.
    /// </summary>
    private static string Clock(string iso)
    {
        if (iso.Length < 16 || iso[10] != 'T')
        {
            return Stamp(iso);
        }

        string zone = Zone(iso);
        return iso[11..16] + (zone.Length == 0 ? "" : zone == "Z" ? " UTC" : " UTC" + zone);
    }

    /// <summary>A stamp's zone: <c>Z</c>, <c>+02:00</c>, or empty.</summary>
    private static string Zone(string iso)
    {
        if (iso.EndsWith('Z'))
        {
            return "Z";
        }

        int sign = Math.Max(iso.LastIndexOf('+'), iso.LastIndexOf('-'));
        return sign > 10 && iso.Length - sign == 6 && iso[sign + 3] == ':' ? iso[sign..] : "";
    }

    private static string List(string words) => string.Join(", ", words.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
