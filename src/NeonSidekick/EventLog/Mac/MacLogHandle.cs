using System.Globalization;

namespace NeonSidekick.EventLog;

/// <summary>
/// The name of one unified-log entry, since the log has no record ids (2026-10-18, the Mac event-log plan): <c>u&lt;unix µs&gt;.&lt;pid&gt;.&lt;thread&gt;.&lt;n&gt;</c>,
/// <c>n</c> counting from 0 among the entries with the same microsecond, pid and thread, in the order a reverse read meets them.
/// <c>eventlog_event</c> and <c>/eventlog</c>'s Enter read the entry again by it, and <c>eventlog_search</c>'s <c>before</c> continues
/// past it. Pure.
/// </summary>
public readonly record struct MacLogHandle(long Microseconds, int Pid, ulong Thread, int Tie)
{
    public const char Lead = 'u';

    /// <summary>The entry's microsecond since the Unix epoch.</summary>
    public static long MicrosecondsOf(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    public static MacLogHandle Of(MacLogEntry entry, int tie)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MacLogHandle(MicrosecondsOf(entry.Time), entry.Pid, entry.Thread, tie);
    }

    /// <summary>The handle's time.</summary>
    public DateTimeOffset Time => DateTimeOffset.UnixEpoch.AddTicks(Microseconds * 10);

    /// <summary>Whether <paramref name="entry"/> has this handle's microsecond, pid and thread (the tie is the reader's count).</summary>
    public bool Names(MacLogEntry entry) =>
        entry is not null && MicrosecondsOf(entry.Time) == Microseconds && entry.Pid == Pid && entry.Thread == Thread;

    public override string ToString() =>
        Lead + Microseconds.ToString(CultureInfo.InvariantCulture) + "." + Pid.ToString(CultureInfo.InvariantCulture) + "."
        + Thread.ToString(CultureInfo.InvariantCulture) + "." + Tie.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a handle back; false for anything not written by <see cref="ToString"/>.</summary>
    public static bool TryParse(string? text, out MacLogHandle handle)
    {
        handle = default;
        string value = (text ?? "").Trim();
        if (value.Length < 8 || value[0] != Lead)
        {
            return false;
        }

        string[] parts = value[1..].Split('.');
        if (parts.Length != 4
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long microseconds)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong thread)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int tie)
            || microseconds <= 0 || tie > 100_000)
        {
            return false;
        }

        handle = new MacLogHandle(microseconds, pid, thread, tie);
        return true;
    }
}
