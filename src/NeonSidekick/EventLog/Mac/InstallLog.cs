using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.EventLog;

/// <summary>
/// One entry of <c>/var/log/install.log</c> (2026-10-18, the Mac event-log plan's phase 3): where its first line starts (the handle's
/// offset), its time, the host, the process (<c>Installer Progress</c> has a space) and pid when the line names them, and its message,
/// the lines after the first (a plist, a product list) joined on, cut at <see cref="MacLogLimits.MessageChars"/>.
/// </summary>
public sealed record InstallEntry(long Offset, DateTimeOffset Time, string Host, string Process, int? Pid, string Message);

/// <summary>
/// <c>install.log</c> read newest first (2026-10-18, the Mac event-log plan's phase 3): backward from the end (or from a page's cursor)
/// in <see cref="Chunk"/>-byte reads, so a short window never reads the whole file (23 MB on the Mac of the plan, since June). A line
/// starts an entry when it opens with the log's own time (<c>2026-10-18 02:28:55-05 host process[pid]: message</c>); any other line
/// continues the entry above it. Lines are split on bytes and each decoded on its own, so a read's edge never cuts a character. An
/// entry's handle is <c>i&lt;offset&gt;.&lt;unix seconds&gt;</c>: the time is checked again when it is read back, so a rotated file says
/// the entry is gone rather than showing another.
/// <para>
/// Two fixes (2026-10-20, the code review). An entry's continued lines past the message's cap keep their top, as <see cref="At"/> does:
/// the backward read meets them bottom first and used to keep the bottom, so a search and <c>eventlog_event</c> showed different text and
/// a text search missed what was near the top. And a first line whose time cannot be a moment (an offset past ±14:00, the year 1 moved
/// before the calendar) is no entry, never an <see cref="ArgumentException"/> out of the whole read.
/// </para>
/// </summary>
public static partial class InstallLog
{
    /// <summary>One backward read.</summary>
    public const int Chunk = 64 * 1024;

    /// <summary>A line longer than this is cut (one entry's line can carry a whole declaration).</summary>
    public const int MaxLineBytes = 256 * 1024;

    /// <summary>A handle: <c>i23790500.1792306135</c>.</summary>
    public static string Handle(InstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return "i" + entry.Offset.ToString(CultureInfo.InvariantCulture) + "." + entry.Time.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A handle's offset and time; false when the text is not one.</summary>
    public static bool TryParseHandle(string text, out long offset, out long seconds)
    {
        offset = 0;
        seconds = 0;
        if (text is null || !text.StartsWith('i'))
        {
            return false;
        }

        string[] parts = text[1..].Split('.');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            && long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>The entries ending before byte <paramref name="before"/> (the end when null or past it), newest first.</summary>
    public static IEnumerable<InstallEntry> Backward(Stream stream, long? before = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long position = Math.Min(before ?? long.MaxValue, stream.Length);
        byte[] carry = [];
        var pending = new Pending();   // continuation lines met so far, the lowest first
        while (position > 0)
        {
            int size = (int)Math.Min(Chunk, position);
            position -= size;
            var combined = new byte[size + carry.Length];
            stream.Position = position;
            stream.ReadExactly(combined, 0, size);
            carry.CopyTo(combined, size);
            int end = combined.Length;
            for (int i = end - 1; i >= 0; i--)
            {
                if (combined[i] != (byte)'\n')
                {
                    continue;
                }

                if (Line(combined.AsSpan(i + 1, end - i - 1), position + i + 1, pending) is { } entry)
                {
                    yield return entry;
                }

                end = i;
            }

            carry = combined[..end];
            if (carry.Length > MaxLineBytes)
            {
                if (Line(carry.AsSpan(carry.Length - MaxLineBytes), position + carry.Length - MaxLineBytes, pending) is { } cut)
                {
                    yield return cut;
                }

                carry = [];
                pending.Clear();   // the rest of that line is lost; never join its tail to the entry above
            }
        }

        if (carry.Length > 0 && Line(carry, 0, pending) is { } first)
        {
            yield return first;
        }
    }

    /// <summary>The entry whose first line starts at <paramref name="offset"/>, or null when no entry starts there.</summary>
    public static InstallEntry? At(Stream stream, long offset)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (offset < 0 || offset >= stream.Length)
        {
            return null;
        }

        if (offset > 0)
        {
            stream.Position = offset - 1;
            if (stream.ReadByte() != '\n')
            {
                return null;
            }
        }

        stream.Position = offset;
        var lines = new List<string>();
        var bytes = new List<byte>();
        int total = 0;
        while (total < MacLogLimits.MessageChars * 4 + MaxLineBytes)
        {
            int b = stream.ReadByte();
            if (b < 0 || b == '\n')
            {
                string line = Decode(bytes.ToArray());
                bytes.Clear();
                if (lines.Count > 0 && Head(line) is not null)
                {
                    break;   // the next entry
                }

                lines.Add(line);
                if (b < 0)
                {
                    break;
                }

                continue;
            }

            bytes.Add((byte)b);
            total++;
        }

        if (bytes.Count > 0)
        {
            lines.Add(Decode(bytes.ToArray()));
        }

        if (lines.Count == 0 || Head(lines[0]) is not { } head)
        {
            return null;
        }

        var rest = lines.Skip(1).Where(l => l.Length > 0).ToList();
        return head with { Offset = offset, Message = Cap(rest.Count == 0 ? head.Message : head.Message + "\n" + string.Join("\n", rest)) };
    }

    /// <summary>A first line read into an entry (offset 0); null when the line does not open with the log's time.</summary>
    public static InstallEntry? Head(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (HeadLine().Match(line) is not { Success: true } m
            || !DateTime.TryParseExact(m.Groups["moment"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        int hours = int.Parse(m.Groups["h"].Value[1..], CultureInfo.InvariantCulture);
        int minutes = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        var offset = new TimeSpan(hours, minutes, 0) * (m.Groups["h"].Value[0] == '-' ? -1 : 1);
        if (minutes > 59 || offset.Duration() > TimeSpan.FromHours(14))
        {
            return null;   // +14:30 passed the old hours check and threw below
        }

        DateTimeOffset time;
        try
        {
            time = new DateTimeOffset(local, offset);
        }
        catch (ArgumentException)
        {
            return null;   // 0001-01-01 00:00 at +01: before the calendar in UTC
        }

        string rest = m.Groups["rest"].Value;
        string process = "";
        int? pid = null;
        string message = rest;
        if (Speaker().Match(rest) is { Success: true } who)
        {
            process = who.Groups["process"].Value.Trim();
            pid = who.Groups["pid"].Success && int.TryParse(who.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : null;
            message = who.Groups["message"].Value;
        }

        return new InstallEntry(0, time, m.Groups["host"].Value, process, pid, Cap(message));
    }

    private static InstallEntry? Line(ReadOnlySpan<byte> bytes, long offset, Pending pending)
    {
        string line = Decode(bytes);
        if (Head(line) is not { } head)
        {
            if (line.Length > 0)
            {
                pending.Add(line);
            }

            return null;
        }

        string message = head.Message;
        if (pending.Count > 0)
        {
            message = message + "\n" + pending.TakeTopFirst();
        }

        return head with { Offset = offset, Message = Cap(message) };
    }

    /// <summary>
    /// The continued lines met below a first line not yet reached, the lowest first (2026-10-20, the code review): each new one is higher
    /// up, and the lowest are let go once the rest still fill the cap, so what is kept is the top, the part the cap keeps.
    /// </summary>
    private sealed class Pending
    {
        private readonly Queue<string> _lines = new();
        private int _chars;

        public int Count => _lines.Count;

        public void Add(string line)
        {
            _lines.Enqueue(line);
            _chars += line.Length;
            while (_lines.Count > 1 && _chars - _lines.Peek().Length >= MacLogLimits.MessageChars)
            {
                _chars -= _lines.Dequeue().Length;
            }
        }

        /// <summary>The lines top first, and the list emptied.</summary>
        public string TakeTopFirst()
        {
            string joined = string.Join("\n", _lines.Reverse());
            _lines.Clear();
            _chars = 0;
            return joined;
        }

        public void Clear()
        {
            _lines.Clear();
            _chars = 0;
        }
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\r');

    private static string Cap(string text) => text.Length <= MacLogLimits.MessageChars ? text : text[..MacLogLimits.MessageChars];

    [GeneratedRegex(@"^(?<moment>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?<h>[+-]\d{2}):?(?<m>\d{2})? (?<host>\S+)(?: (?<rest>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadLine();

    [GeneratedRegex(@"^(?<process>[^:\[\]]{1,80}?)(?:\[(?<pid>\d{1,9})\])?: ?(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Speaker();
}
