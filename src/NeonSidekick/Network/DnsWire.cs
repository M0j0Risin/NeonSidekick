using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace NeonSidekick.Network;

/// <summary>A DNS record as an answer shows it: the owner name, the type's word, its TTL and its data in words.</summary>
public sealed record DnsRecord(string Name, ushort Type, uint Ttl, string Data)
{
    public string TypeName => DnsWire.TypeName(Type);
}

/// <summary>A DNS reply read: its id, the flags that matter, the question it echoed and its records.</summary>
public sealed record DnsMessage(ushort Id, bool IsResponse, bool Truncated, int Rcode, string QuestionName, ushort QuestionType, ushort QuestionClass, IReadOnlyList<DnsRecord> Answers);

/// <summary>A reply that cannot be read: truncated, a name pointer loop, a name over 255 bytes.</summary>
public sealed class DnsFormatException(string message) : Exception(message);

/// <summary>
/// DNS on the wire for <c>net_dns</c>'s own client (2026-10-15, the admin-tools plan; RFC 1035, EDNS0 per RFC 6891): a query with one
/// question, recursion desired and an OPT record offering 1,232 bytes; a reply's header, question and answer records. Names in a reply are
/// decompressed with a pointer-loop guard (each pointer must go backwards, at most 128 of them) and a 255-byte cap, so a hostile reply can
/// neither loop nor overrun. The types read: A, NS, CNAME, SOA, PTR, MX, TXT, AAAA, SRV and CAA; any other's data as its length. Pure.
/// </summary>
public static class DnsWire
{
    public const ushort A = 1;
    public const ushort Ns = 2;
    public const ushort Cname = 5;
    public const ushort Soa = 6;
    public const ushort Ptr = 12;
    public const ushort Mx = 15;
    public const ushort Txt = 16;
    public const ushort Aaaa = 28;
    public const ushort Srv = 33;
    public const ushort Opt = 41;
    public const ushort Caa = 257;

    public const ushort ClassIn = 1;

    /// <summary>The UDP size offered in the OPT record (the DNS Flag Day 2020 figure).</summary>
    public const ushort UdpSize = 1232;

    /// <summary>The longest name on the wire.</summary>
    public const int MaxName = 255;

    /// <summary>The types <c>net_dns</c> takes, by word.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> Types = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = A,
        ["AAAA"] = Aaaa,
        ["CNAME"] = Cname,
        ["MX"] = Mx,
        ["TXT"] = Txt,
        ["NS"] = Ns,
        ["SOA"] = Soa,
        ["PTR"] = Ptr,
        ["SRV"] = Srv,
        ["CAA"] = Caa,
    };

    /// <summary>A type's word: <c>MX</c>; <c>TYPE99</c> for one not known.</summary>
    public static string TypeName(ushort type) =>
        Types.FirstOrDefault(p => p.Value == type).Key is { } name ? name.ToUpperInvariant() : "TYPE" + type.ToString(CultureInfo.InvariantCulture);

    /// <summary>The name a PTR lookup asks for an address: <c>4.3.2.1.in-addr.arpa</c>, or the nibbles under <c>ip6.arpa</c>.</summary>
    public static string ReverseName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        address = HostRules.Canonical(address);
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return string.Join('.', bytes.Reverse().Select(b => b.ToString(CultureInfo.InvariantCulture))) + ".in-addr.arpa";
        }

        var nibbles = new StringBuilder(72);
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            nibbles.Append((bytes[i] & 0x0F).ToString("x", CultureInfo.InvariantCulture)).Append('.');
            nibbles.Append((bytes[i] >> 4).ToString("x", CultureInfo.InvariantCulture)).Append('.');
        }

        return nibbles + "ip6.arpa";
    }

    /// <summary>
    /// The address a reverse name stands for, <see cref="ReverseName"/> read back (2026-10-19, the network review: a PTR typed as
    /// <c>1.1.168.192.in-addr.arpa</c> was judged as a name, not as the LAN address it asks about): any name under <c>in-addr.arpa</c> or
    /// <c>ip6.arpa</c>, of any type, its parts read from the zone down until one is not an octet or a nibble, the rest taken as zeros — so a
    /// zone (<c>168.192.in-addr.arpa</c>) stands for its first address. Null for any other name.
    /// </summary>
    public static IPAddress? ReverseAddress(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string[] labels = name.Trim().TrimEnd('.').Split('.');
        bool v4 = labels.Length >= 2 && labels[^2].Equals("in-addr", StringComparison.OrdinalIgnoreCase) && labels[^1].Equals("arpa", StringComparison.OrdinalIgnoreCase);
        bool v6 = labels.Length >= 2 && labels[^2].Equals("ip6", StringComparison.OrdinalIgnoreCase) && labels[^1].Equals("arpa", StringComparison.OrdinalIgnoreCase);
        if (!v4 && !v6)
        {
            return null;
        }

        var bytes = new byte[v4 ? 4 : 16];
        int parts = v4 ? 4 : 32;
        for (int i = 0; i < parts && labels.Length - 3 - i >= 0; i++)
        {
            string label = labels[labels.Length - 3 - i];
            if (v4)
            {
                if (label.Length is 0 or > 3 || !label.All(char.IsAsciiDigit) || !byte.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out byte octet))
                {
                    break;
                }

                bytes[i] = octet;
            }
            else
            {
                if (label.Length != 1 || !char.IsAsciiHexDigit(label[0]))
                {
                    break;
                }

                int nibble = Convert.ToInt32(label, 16);
                bytes[i / 2] |= (byte)(i % 2 == 0 ? nibble << 4 : nibble);
            }
        }

        return new IPAddress(bytes);
    }

    /// <summary>A query for <paramref name="name"/>/<paramref name="type"/> with <paramref name="id"/>, recursion desired, an EDNS0 OPT record.</summary>
    public static byte[] Query(ushort id, string name, ushort type)
    {
        ArgumentNullException.ThrowIfNull(name);
        var labels = EncodeName(name);
        var packet = new byte[12 + labels.Length + 4 + 11];
        var span = packet.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, id);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x0100);   // RD
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);        // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 1);       // ARCOUNT: the OPT record
        labels.CopyTo(span[12..]);
        int at = 12 + labels.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[at..], type);
        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 2)..], ClassIn);
        at += 4;
        // OPT: root name (0), type 41, class = UDP size, TTL 0 (extended RCODE, version, flags), RDLENGTH 0.
        span[at] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 1)..], Opt);
        BinaryPrimitives.WriteUInt16BigEndian(span[(at + 3)..], UdpSize);
        return packet;
    }

    /// <summary>A name as labels on the wire; a name that cannot be one throws <see cref="ArgumentException"/>.</summary>
    public static byte[] EncodeName(string name)
    {
        string trimmed = name.Trim().TrimEnd('.');
        var output = new List<byte>(trimmed.Length + 2);
        if (trimmed.Length > 0)
        {
            foreach (string label in trimmed.Split('.'))
            {
                var bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63)
                {
                    throw new ArgumentException(NetworkText.BadName(name), nameof(name));
                }

                output.Add((byte)bytes.Length);
                output.AddRange(bytes);
            }
        }

        output.Add(0);
        if (output.Count > MaxName)
        {
            throw new ArgumentException(NetworkText.BadName(name), nameof(name));
        }

        return output.ToArray();
    }

    /// <summary>A reply read whole; <see cref="DnsFormatException"/> for one that cannot be.</summary>
    public static DnsMessage Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12)
        {
            throw new DnsFormatException(NetworkText.DnsTruncatedReply);
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int at = 12;
        string questionName = "";
        ushort questionType = 0, questionClass = 0;
        for (int q = 0; q < questions; q++)
        {
            string name = ReadName(packet, ref at);
            if (at + 4 > packet.Length)
            {
                throw new DnsFormatException(NetworkText.DnsTruncatedReply);
            }

            if (q == 0)
            {
                questionName = name;
                questionType = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
                questionClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
            }

            at += 4;
        }

        var records = new List<DnsRecord>(answers);
        for (int r = 0; r < answers; r++)
        {
            string owner = ReadName(packet, ref at);
            if (at + 10 > packet.Length)
            {
                throw new DnsFormatException(NetworkText.DnsTruncatedReply);
            }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(at + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 8)..]);
            at += 10;
            if (at + length > packet.Length)
            {
                throw new DnsFormatException(NetworkText.DnsTruncatedReply);
            }

            records.Add(new DnsRecord(owner, type, ttl, ReadData(packet, at, length, type)));
            at += length;
        }

        return new DnsMessage(id, (flags & 0x8000) != 0, (flags & 0x0200) != 0, flags & 0x000F, questionName, questionType, questionClass, records);
    }

    /// <summary>A name at <paramref name="at"/>, pointers followed backwards only, at most 128 of them, 255 bytes at most; <paramref name="at"/> left past it.</summary>
    public static string ReadName(ReadOnlySpan<byte> packet, ref int at)
    {
        var name = new StringBuilder();
        int position = at;
        int end = -1;
        int jumps = 0;
        int length = 0;
        while (true)
        {
            if (position >= packet.Length)
            {
                throw new DnsFormatException(NetworkText.DnsTruncatedReply);
            }

            byte b = packet[position];
            if ((b & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length)
                {
                    throw new DnsFormatException(NetworkText.DnsTruncatedReply);
                }

                int target = ((b & 0x3F) << 8) | packet[position + 1];
                if (target >= position || ++jumps > 128)
                {
                    throw new DnsFormatException(NetworkText.DnsPointerLoop);
                }

                if (end < 0)
                {
                    end = position + 2;
                }

                position = target;
                continue;
            }

            if ((b & 0xC0) != 0)
            {
                throw new DnsFormatException(NetworkText.DnsTruncatedReply);
            }

            if (b == 0)
            {
                at = end >= 0 ? end : position + 1;
                return name.Length == 0 ? "." : name.ToString();
            }

            length += b + 1;
            if (length > MaxName || position + 1 + b > packet.Length)
            {
                throw new DnsFormatException(length > MaxName ? NetworkText.DnsNameTooLong : NetworkText.DnsTruncatedReply);
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            foreach (byte c in packet.Slice(position + 1, b))
            {
                name.Append(c is >= 0x21 and <= 0x7E ? (char)c : '?');
            }

            position += 1 + b;
        }
    }

    private static string ReadData(ReadOnlySpan<byte> packet, int at, int length, ushort type)
    {
        var data = packet.Slice(at, length);
        int cursor = at;
        switch (type)
        {
            case A when length == 4:
            case Aaaa when length == 16:
                return new IPAddress(data).ToString();
            case Ns:
            case Cname:
            case Ptr:
                return ReadName(packet, ref cursor);
            case Mx when length >= 3:
                ushort preference = BinaryPrimitives.ReadUInt16BigEndian(data);
                cursor += 2;
                return preference.ToString(CultureInfo.InvariantCulture) + " " + ReadName(packet, ref cursor);
            case Srv when length >= 7:
                ushort priority = BinaryPrimitives.ReadUInt16BigEndian(data);
                ushort weight = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
                cursor += 6;
                return priority.ToString(CultureInfo.InvariantCulture) + " " + weight.ToString(CultureInfo.InvariantCulture) + " " + port.ToString(CultureInfo.InvariantCulture) + " " + ReadName(packet, ref cursor);
            case Soa:
                string primary = ReadName(packet, ref cursor);
                string mailbox = ReadName(packet, ref cursor);
                if (cursor + 20 > at + length)
                {
                    throw new DnsFormatException(NetworkText.DnsTruncatedReply);
                }

                uint serial = BinaryPrimitives.ReadUInt32BigEndian(packet[cursor..]);
                uint refresh = BinaryPrimitives.ReadUInt32BigEndian(packet[(cursor + 4)..]);
                uint retry = BinaryPrimitives.ReadUInt32BigEndian(packet[(cursor + 8)..]);
                uint expire = BinaryPrimitives.ReadUInt32BigEndian(packet[(cursor + 12)..]);
                uint minimum = BinaryPrimitives.ReadUInt32BigEndian(packet[(cursor + 16)..]);
                return string.Join(' ', primary, mailbox, serial.ToString(CultureInfo.InvariantCulture), refresh.ToString(CultureInfo.InvariantCulture),
                    retry.ToString(CultureInfo.InvariantCulture), expire.ToString(CultureInfo.InvariantCulture), minimum.ToString(CultureInfo.InvariantCulture));
            case Txt:
                var strings = new List<string>();
                int i = 0;
                while (i < data.Length)
                {
                    int n = data[i];
                    if (i + 1 + n > data.Length)
                    {
                        throw new DnsFormatException(NetworkText.DnsTruncatedReply);
                    }

                    strings.Add(Encoding.UTF8.GetString(data.Slice(i + 1, n)));
                    i += 1 + n;
                }

                return string.Concat(strings);
            case Caa when length >= 2:
                byte flags = data[0];
                int tagLength = data[1];
                if (2 + tagLength > data.Length)
                {
                    throw new DnsFormatException(NetworkText.DnsTruncatedReply);
                }

                string tag = Encoding.ASCII.GetString(data.Slice(2, tagLength));
                string value = Encoding.UTF8.GetString(data[(2 + tagLength)..]);
                return flags.ToString(CultureInfo.InvariantCulture) + " " + tag + " \"" + value + "\"";
            default:
                return "(" + length.ToString(CultureInfo.InvariantCulture) + " bytes)";
        }
    }
}
