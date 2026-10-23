using System.Globalization;
using System.Xml;

namespace NeonSidekick.EventLog;

/// <summary>
/// One named value of an event: an EventData <c>Data</c>, a classic event's numbered one, or a UserData leaf; <see cref="Binary"/> for a classic
/// event's raw <c>&lt;Binary&gt;</c> data, as hex.
/// </summary>
public sealed record EventField(string Name, string Value, bool Binary = false);

/// <summary>What <see cref="EventXml.Parse"/> reads of an event's XML besides its system values.</summary>
public sealed record EventXmlData(IReadOnlyList<EventField> Fields, string? Message, string? SourceName)
{
    public static readonly EventXmlData Empty = new([], null, null);
}

/// <summary>
/// The event's XML as <c>EvtRenderEventXml</c> gives it (2026-10-15, the event-log plan), read with <see cref="XmlReader"/> held tight —
/// no DTD, no resolver, characters not checked (an event may carry a control character, which the answer scrubs later). The plan left
/// room for a hand-written reader; <see cref="XmlReader"/> is already linked into the exe and its entity and <c>&amp;#x…;</c> handling is
/// exactly what a hand-written one would have had to copy, so it is the one used. Read: EventData's <c>Data[@Name]</c> (a classic event's
/// unnamed ones as <c>Data1</c>, <c>Data2</c>…, its <c>Binary</c> as one), UserData's leaves flattened to their element names
/// (<c>LogFileCleared/SubjectUserName</c> as <c>SubjectUserName</c>), <c>RenderingInfo/Message</c> (a forwarded event's own text), and the
/// provider's <c>EventSourceName</c>. <see cref="System"/> reads the system values (the fake reader's whole source). A malformed document is
/// what was read before the fault, never a throw. Pure.
/// </summary>
public static class EventXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    /// <summary>The fields, the rendered message and the source name of <paramref name="xml"/>.</summary>
    public static EventXmlData Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var fields = new List<EventField>();
        string? message = null;
        string? source = null;
        int unnamed = 0;
        // The open elements: Event, then its section (System, EventData, UserData, RenderingInfo), then deeper.
        var stack = new List<Frame>(8);
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        if (stack.Count > 0)
                        {
                            stack[^1].HasChild = true;
                        }

                        string name = reader.LocalName;
                        string? section = stack.Count >= 2 ? stack[1].Name : stack.Count == 1 ? name : null;
                        if (stack.Count == 2 && section == "System" && name == "Provider")
                        {
                            source = reader.GetAttribute("EventSourceName") is { Length: > 0 } s ? s : source;
                        }

                        var frame = new Frame(name, name == "Data" ? reader.GetAttribute("Name") : null);
                        if (reader.IsEmptyElement)
                        {
                            Close(frame, stack);
                        }
                        else
                        {
                            stack.Add(frame);
                        }

                        break;
                    }

                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (stack.Count > 0)
                        {
                            stack[^1].Text.Append(reader.Value);
                        }

                        break;

                    case XmlNodeType.EndElement when stack.Count > 0:
                    {
                        var frame = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        Close(frame, stack);
                        break;
                    }
                }
            }
        }
        catch (XmlException)
        {
            // What was read before the fault stands.
        }

        return new EventXmlData(fields, string.IsNullOrEmpty(message) ? null : message, source);

        // An element ended (its parents are stack): a field, the message, or nothing.
        void Close(Frame frame, List<Frame> parents)
        {
            string? section = parents.Count >= 2 ? parents[1].Name : null;
            if (parents.Count == 2 && parents[1].Name == "EventData" && frame.Name is "Data" or "Binary")
            {
                string? fieldName = frame.Name == "Binary" ? "Binary" : frame.DataName;
                fields.Add(new EventField(string.IsNullOrEmpty(fieldName) ? "Data" + (++unnamed).ToString(CultureInfo.InvariantCulture) : fieldName, frame.Text.ToString(), frame.Name == "Binary"));
            }
            else if (parents.Count >= 2 && section == "UserData" && !frame.HasChild)
            {
                // A Data element inside UserData named by its Name attribute (2026-10-23, the admin review's W4: it read as "Data", so a
                // <Data Name="Password"> was never a secret's name, nor matched by field:Password).
                fields.Add(new EventField(frame.Name == "Data" && !string.IsNullOrEmpty(frame.DataName) ? frame.DataName : frame.Name, frame.Text.ToString()));
            }
            else if (parents.Count == 2 && section == "RenderingInfo" && frame.Name == "Message")
            {
                message = frame.Text.ToString();
            }
        }
    }

    /// <summary>An open element while <see cref="Parse"/> reads: its name, a <c>Data</c>'s <c>Name</c>, its text so far, whether it held an element.</summary>
    private sealed class Frame(string name, string? dataName)
    {
        public string Name { get; } = name;

        public string? DataName { get; } = dataName;

        public System.Text.StringBuilder Text { get; } = new();

        public bool HasChild { get; set; }
    }

    /// <summary>
    /// The system values of <paramref name="xml"/> (the fake reader's, and a forwarded event's check): provider, id, level, keywords,
    /// time, record, channel, computer, the user's SID, task, opcode, process and thread. A value that is missing or unreadable is the default.
    /// </summary>
    public static EventSystemValues System(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        string provider = "", channel = "", computer = "";
        string? sid = null;
        int id = 0, level = 0, task = 0, opcode = 0;
        int? process = null, thread = null;
        ulong keywords = 0;
        long record = 0;
        DateTimeOffset? time = null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            bool inSystem = false;
            string? current = null;
            var text = new System.Text.StringBuilder();
            // One pass over the nodes, never a content read (2026-10-15: ReadElementContentAsString leaves the reader on the next element,
            // which the loop's Read then stepped over, so every other value was lost): an element's text is gathered until its end tag.
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element when reader.LocalName == "System":
                        inSystem = true;
                        break;
                    case XmlNodeType.Element when inSystem:
                        switch (reader.LocalName)
                        {
                            case "Provider":
                                provider = reader.GetAttribute("Name") ?? "";
                                break;
                            case "TimeCreated":
                                time = DateTimeOffset.TryParse(reader.GetAttribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
                                break;
                            case "Execution":
                                process = int.TryParse(reader.GetAttribute("ProcessID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : null;
                                thread = int.TryParse(reader.GetAttribute("ThreadID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int th) ? th : null;
                                break;
                            case "Security":
                                sid = reader.GetAttribute("UserID") is { Length: > 0 } u ? u : null;
                                break;
                        }

                        current = reader.IsEmptyElement ? null : reader.LocalName;
                        text.Clear();
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when current is not null:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement when reader.LocalName == "System":
                        inSystem = false;
                        break;
                    case XmlNodeType.EndElement when current == reader.LocalName:
                        string value = text.ToString().Trim();
                        switch (current)
                        {
                            case "EventID":
                                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                                break;
                            case "Level":
                                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                                break;
                            case "Task":
                                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out task);
                                break;
                            case "Opcode":
                                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out opcode);
                                break;
                            case "Keywords":
                                ulong.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out keywords);
                                break;
                            case "EventRecordID":
                                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out record);
                                break;
                            case "Channel":
                                channel = value;
                                break;
                            case "Computer":
                                computer = value;
                                break;
                        }

                        current = null;
                        break;
                }

                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "System")
                {
                    break;   // the system part read; the rest is Parse's
                }
            }
        }
        catch (XmlException)
        {
            // What was read before the fault stands.
        }

        return new EventSystemValues(provider, id, level, keywords, time, record, channel, computer, sid, task, opcode, process, thread);
    }
}
