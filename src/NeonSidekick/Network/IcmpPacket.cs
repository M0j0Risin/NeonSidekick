using System.Buffers.Binary;

namespace NeonSidekick.Network;

/// <summary>What one received ICMP packet was, as far as our echo is concerned.</summary>
public enum IcmpReplyKind
{
    /// <summary>Not ours, or not ICMP at all: keep waiting.</summary>
    None,

    /// <summary>The echo reply to our request.</summary>
    EchoReply,

    /// <summary>A router's Time Exceeded carrying our request: a hop of a trace.</summary>
    TimeExceeded,

    /// <summary>A Destination Unreachable carrying our request.</summary>
    Unreachable,
}

/// <summary>
/// The ICMPv4 echo on the wire (2026-10-15, the admin-tools plan: the Mac's pinger builds its own, <see cref="IcmpSocketPinger"/>): an echo
/// request with its checksum, and a reply read with or without the IP header in front (macOS hands a datagram socket the header), a Time
/// Exceeded or Destination Unreachable recognized by the original request embedded in it — our id and sequence. Pure.
/// </summary>
public static class IcmpPacket
{
    public const byte EchoReplyType = 0;
    public const byte UnreachableType = 3;
    public const byte EchoRequestType = 8;
    public const byte TimeExceededType = 11;

    /// <summary>The ICMP header's length.</summary>
    public const int HeaderLength = 8;

    /// <summary>An echo request: type 8, code 0, the checksum, <paramref name="id"/>, <paramref name="sequence"/>, then <paramref name="payload"/>.</summary>
    public static byte[] Echo(ushort id, ushort sequence, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[HeaderLength + payload.Length];
        packet[0] = EchoRequestType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), sequence);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Checksum(packet));
        return packet;
    }

    /// <summary>The Internet checksum (RFC 1071) of <paramref name="data"/>: the ones' complement of the ones' complement sum of its 16-bit words.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
        }

        if (i < data.Length)
        {
            sum += (uint)(data[i] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>
    /// What <paramref name="packet"/> says about our echo <paramref name="id"/>/<paramref name="sequence"/>: its reply, a Time Exceeded or an
    /// Unreachable carrying it, or <see cref="IcmpReplyKind.None"/> for anything else (another program's echo, a truncated packet). The IP
    /// header in front is skipped when there is one (a first nibble of 4 and a sane header length).
    /// </summary>
    public static (IcmpReplyKind Kind, byte Code) Parse(ReadOnlySpan<byte> packet, ushort id, ushort sequence)
    {
        var icmp = StripIpHeader(packet);
        if (icmp.Length < HeaderLength)
        {
            return (IcmpReplyKind.None, 0);
        }

        byte type = icmp[0];
        byte code = icmp[1];
        switch (type)
        {
            case EchoReplyType:
                return Matches(icmp, id, sequence) ? (IcmpReplyKind.EchoReply, code) : (IcmpReplyKind.None, (byte)0);
            case TimeExceededType:
            case UnreachableType:
                // The original datagram follows the 8-byte header: its IP header, then at least 8 bytes of our echo request.
                var original = icmp[HeaderLength..];
                // The quoted datagram must be ICMP (protocol 1), ours (2026-10-23, the admin review's P14: another program's UDP traceroute error
                // was read as our hop).
                if (original.Length < 20 || original[0] >> 4 != 4 || original[9] != 1)
                {
                    return (IcmpReplyKind.None, 0);
                }

                int inner = (original[0] & 0x0F) * 4;
                if (inner < 20 || original.Length < inner + HeaderLength)
                {
                    return (IcmpReplyKind.None, 0);
                }

                var request = original[inner..];
                if (request[0] != EchoRequestType || !Matches(request, id, sequence))
                {
                    return (IcmpReplyKind.None, 0);
                }

                return (type == TimeExceededType ? IcmpReplyKind.TimeExceeded : IcmpReplyKind.Unreachable, code);
            default:
                return (IcmpReplyKind.None, 0);
        }
    }

    /// <summary>The ICMP part of <paramref name="packet"/>: past an IPv4 header when one leads it.</summary>
    public static ReadOnlySpan<byte> StripIpHeader(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 20 && packet[0] >> 4 == 4)
        {
            int length = (packet[0] & 0x0F) * 4;
            if (length >= 20 && packet.Length >= length && packet[9] == 1)
            {
                return packet[length..];
            }
        }

        return packet;
    }

    /// <summary>Whether an echo reply carries the request's own payload (C14): the bytes after both ICMP headers.</summary>
    public static bool SamePayload(ReadOnlySpan<byte> reply, ReadOnlySpan<byte> request)
    {
        var icmp = StripIpHeader(reply);
        return icmp.Length >= HeaderLength && request.Length >= HeaderLength && icmp[HeaderLength..].SequenceEqual(request[HeaderLength..]);
    }

    private static bool Matches(ReadOnlySpan<byte> icmp, ushort id, ushort sequence) =>
        BinaryPrimitives.ReadUInt16BigEndian(icmp[4..]) == id && BinaryPrimitives.ReadUInt16BigEndian(icmp[6..]) == sequence;
}
