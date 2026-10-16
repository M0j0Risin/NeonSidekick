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
                "ScheduleByMonth" => "every month" + (Get(v, "DaysOfMonth") is { Length: > 0 } dom ? " on day " + List(dom) : "") + (Get(v, "Months") is { Length: > 0 } months && months.Split(' ').Length < 12 ? " in " + List(months) : "") + at,
                "ScheduleByMonthDayOfWeek" => "every month on the " + List(Get(v, "Week") ?? Get(v, "Weeks") ?? "") + " " + List(Get(v, "DaysOfWeek") ?? "") + at,
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
        if (Get(v, "Delay") is { } delay)
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
        0x80041314 => "was stopped: it ran past its time limit",
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

            return Count((int)span.TotalSeconds, "second");
        }
        catch (FormatException)
        {
            return iso;
        }
    }

    /// <summary>An ISO stamp as the computer wrote it, the T made a space and the seconds dropped when zero.</summary>
    public static string Stamp(string iso)
    {
        string text = iso.Replace('T', ' ');
        return text.Length >= 19 && text.EndsWith(":00", StringComparison.Ordinal) && text.Length == 19 ? text[..16] : text;
    }

    private static string Clock(string iso) => iso.Length >= 16 && iso[10] == 'T' ? iso[11..16] : Stamp(iso);

    private static string List(string words) => string.Join(", ", words.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    private static string Count(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
