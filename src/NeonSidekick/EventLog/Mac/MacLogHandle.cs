using System.Globalization;

namespace NeonSidekick.EventLog;

/// <summary>
/// The name of one unified-log entry, since the log has no record ids (2026-10-18, the Mac event-log plan): <c>u&lt;unix µs&gt;.&lt;pid&gt;.&lt;thread&gt;.&lt;n&gt;.&lt;mark&gt;</c>,
/// <c>n</c> counting from 0 among the entries with the same microsecond, pid, thread and mark, in the order a reverse read meets them.
/// <c>eventlog_event</c> and <c>/eventlog</c>'s Enter read the entry again by it, and <c>eventlog_search</c>'s <c>before</c> continues
/// past it. Pure.
/// <para>
/// The mark (2026-10-20, the code review): eight hex digits of an FNV-1a hash over the entry's level, subsystem, category and message as
/// the redacted detail shows it. Before it, <c>n</c> was counted among the entries a search's filter let through, and the find (every
/// level, nothing else narrowing it) counted another set, so a default entry and an error from one thread in one microsecond could swap
/// and the wrong one came back silently. Every term a filter has is a function of what the mark hashes (the pid fixes the process), so
/// entries with one mark are kept or dropped together and their count is the same under any filter. The redacted message, never the
/// raw one, so a twin cannot test guesses at a hidden value against the hash. A handle written before the mark (four parts) still reads,
/// counted the old way.
/// </para>
/// </summary>
public readonly record struct MacLogHandle(long Microseconds, int Pid, ulong Thread, int Tie, uint? Mark = null)
{
    public const char Lead = 'u';

    /// <summary>
    /// The latest microsecond a handle may name (2026-10-20, the code review: <c>u999999999999999999.1.1.0</c> threw from
    /// <see cref="Time"/>): one short of what a <see cref="DateTimeOffset"/> holds, so the read's <c>until</c> a microsecond past it does too.
    /// </summary>
    public static readonly long MaxMicroseconds = (DateTimeOffset.MaxValue.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10 - 1;

    /// <summary>The entry's microsecond since the Unix epoch.</summary>
    public static long MicrosecondsOf(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    /// <summary>The handle of <paramref name="entry"/>, the <paramref name="tie"/>th of its microsecond, pid, thread and mark.</summary>
    public static MacLogHandle Of(MacLogEntry entry, int tie)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new MacLogHandle(MicrosecondsOf(entry.Time), entry.Pid, entry.Thread, tie, MarkOf(entry));
    }

    /// <summary>
    /// The entry's mark: FNV-1a (32 bits) over its level, subsystem, category and redacted message, each followed by a NUL. Stable across
    /// runs and machines (never <see cref="string.GetHashCode()"/>, which is salted per process).
    /// </summary>
    /// <remarks>The reader's own mark, over the whole message, when it gave one (X5): the copy's message is cut, so entries alike in their first part shared a mark.</remarks>
    public static uint MarkOf(MacLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Mark ?? MarkOf(entry.Level, entry.Subsystem, entry.Category, entry.Message);
    }

    /// <summary>The mark of an entry's parts (the reader's, over its whole message).</summary>
    public static uint MarkOf(int level, string subsystem, string category, string message)
    {
        uint hash = 2_166_136_261;
        void Add(string text)
        {
            foreach (char c in text)
            {
                hash = (hash ^ (byte)c) * 16_777_619;
                hash = (hash ^ (byte)(c >> 8)) * 16_777_619;
            }

            hash = (hash ^ 0) * 16_777_619;
        }

        Add(level.ToString(CultureInfo.InvariantCulture));
        Add(subsystem);
        Add(category);
        Add(EventLogRedaction.Text(message).TrimEnd());
        return hash;
    }

    /// <summary>The handle's time.</summary>
    public DateTimeOffset Time => DateTimeOffset.UnixEpoch.AddTicks(Microseconds * 10);

    /// <summary>
    /// Whether <paramref name="entry"/> has this handle's microsecond, pid, thread and (when the handle has one) mark; the tie is the
    /// reader's count.
    /// </summary>
    public bool Names(MacLogEntry entry) =>
        entry is not null && MicrosecondsOf(entry.Time) == Microseconds && entry.Pid == Pid && entry.Thread == Thread
        && (Mark is not { } mark || MarkOf(entry) == mark);

    public override string ToString() =>
        Lead + Microseconds.ToString(CultureInfo.InvariantCulture) + "." + Pid.ToString(CultureInfo.InvariantCulture) + "."
        + Thread.ToString(CultureInfo.InvariantCulture) + "." + Tie.ToString(CultureInfo.InvariantCulture)
        + (Mark is { } mark ? "." + mark.ToString("x8", CultureInfo.InvariantCulture) : "");

    /// <summary>Reads a handle back (with its mark, or a four-part one from before it); false for anything not written by <see cref="ToString"/>.</summary>
    public static bool TryParse(string? text, out MacLogHandle handle)
    {
        handle = default;
        string value = (text ?? "").Trim();
        if (value.Length < 8 || value[0] != Lead)
        {
            return false;
        }

        string[] parts = value[1..].Split('.');
        uint mark = 0;
        if (parts.Length is not (4 or 5)
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long microseconds)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || !ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong thread)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int tie)
            || (parts.Length == 5 && (parts[4].Length != 8 || !uint.TryParse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mark)))
            || microseconds <= 0 || microseconds > MaxMicroseconds || tie > 100_000)
        {
            return false;
        }

        handle = new MacLogHandle(microseconds, pid, thread, tie, parts.Length == 5 ? mark : null);
        return true;
    }
}
