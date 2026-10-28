using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Monitors;

/// <summary>
/// DDC/CI's frames by hand (2026-10-27, the NVIDIA plan: a monitor off the desktop reached over its cable's I2C, where dxva2 does the framing
/// itself): the three requests the app sends — Get VCP 0x60 (the input), Set VCP 0x60 and the capabilities request — and their replies'
/// checks. A request goes to I2C address 0x6E with 0x51 (the host's address) as its one-byte register; its bytes are the length (0x80 | n),
/// the body and a checksum XOR'd from 0x6E ^ 0x51. A reply starts at the display's 0x6E, then the length, the body, and a checksum XOR'd from
/// 0x50. No other VCP code is built here, so this path can do nothing the input switch can't (a safety fact). The frames and waits are the
/// spike's, proven on this PC's XG27UQDMS and LG ULTRAWIDE. Pure.
/// </summary>
public static class DdcPacket
{
    /// <summary>The monitor's I2C address (DDC/CI's 0x37, shifted).</summary>
    public const byte DisplayAddress = 0x6E;

    /// <summary>The host's address, sent as the request's one-byte register.</summary>
    public const byte HostAddress = 0x51;

    /// <summary>A Get VCP reply's length: source, length, opcode, result, code, type, maximum (2), value (2), checksum.</summary>
    public const int InputReplyLength = 11;

    /// <summary>A capabilities fragment's read: source, length, opcode, offset (2), up to 32 data bytes, checksum.</summary>
    public const int FragmentReadLength = 38;

    /// <summary>How many times one fragment is asked for before the read gives up.</summary>
    public const int FragmentTries = 3;

    /// <summary>The most fragments one capabilities read takes (a string of up to 4 KB) before it counts as never ending.</summary>
    public const int MaxFragments = 128;

    private const byte GetVcp = 0x01;
    private const byte GetVcpReply = 0x02;
    private const byte SetVcp = 0x03;
    private const byte CapabilitiesRequest = 0xF3;
    private const byte CapabilitiesReply = 0xE3;

    /// <summary>Get VCP 0x60: <c>82 01 60</c> and its checksum.</summary>
    public static byte[] GetInput() => Frame([GetVcp, Mccs.InputSource]);

    /// <summary>Set VCP 0x60 to <paramref name="code"/>: <c>84 03 60 00 &lt;code&gt;</c> and its checksum. A Set has no reply.</summary>
    public static byte[] SetInput(byte code) => Frame([SetVcp, Mccs.InputSource, 0x00, code]);

    /// <summary>The capabilities request for the fragment at <paramref name="offset"/>: <c>83 F3 &lt;hi&gt; &lt;lo&gt;</c> and its checksum.</summary>
    public static byte[] Capabilities(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, ushort.MaxValue);
        return Frame([CapabilitiesRequest, (byte)(offset >> 8), (byte)offset]);
    }

    /// <summary>
    /// The input a Get VCP 0x60 reply gives (its value's low byte), or null when the reply is short, its checksum or opcode wrong, its result
    /// code non-zero (the monitor doesn't support the code) or it answers another code.
    /// </summary>
    public static byte? ReadInput(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < InputReplyLength || reply[0] != DisplayAddress || (reply[1] & 0x7F) != InputReplyLength - 3
            || !Checked(reply[..InputReplyLength]) || reply[2] != GetVcpReply || reply[3] != 0 || reply[4] != Mccs.InputSource)
        {
            return null;
        }

        return reply[9];
    }

    /// <summary>
    /// The data of a capabilities fragment read for <paramref name="offset"/> (empty for the last), or null when the reply is short, its
    /// checksum or opcode wrong, or it answers another offset.
    /// </summary>
    public static byte[]? ReadFragment(ReadOnlySpan<byte> reply, int offset)
    {
        if (reply.Length < 6 || reply[0] != DisplayAddress)
        {
            return null;
        }

        int length = reply[1] & 0x7F;
        if (length < 3 || reply.Length < length + 3 || !Checked(reply[..(length + 3)]) || reply[2] != CapabilitiesReply || ((reply[3] << 8) | reply[4]) != offset)
        {
            return null;
        }

        return reply.Slice(5, length - 3).ToArray();
    }

    /// <summary>
    /// The whole capabilities read: fragments asked for from offset 0 through <paramref name="fragment"/> (the raw read for an offset, null when
    /// the bus failed) until an empty one or one holding the NUL that ends the string, each tried up to <see cref="FragmentTries"/> times;
    /// the bytes joined, cut at the first NUL (as dxva2's buffer is cut: the LG's last fragments carry <c>00 FF</c>), scrubbed and read for
    /// their inputs. Null when a fragment never came back right or the string never ended within <see cref="MaxFragments"/>. Nothing is asked
    /// past the NUL (2026-10-27, the review: the bus stayed held for fragments the cut dropped anyway).
    /// </summary>
    public static MonitorCaps? ReadCapabilities(Func<int, byte[]?> fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var joined = new List<byte>();
        for (int count = 0; count < MaxFragments; count++)
        {
            byte[]? data = null;
            for (int attempt = 0; attempt < FragmentTries && data is null; attempt++)
            {
                data = fragment(joined.Count) is { } read ? ReadFragment(read, joined.Count) : null;
            }

            if (data is null)
            {
                return null;
            }

            joined.AddRange(data);
            if (data.Length == 0 || Array.IndexOf(data, (byte)0) >= 0)
            {
                return Caps(joined);
            }
        }

        return null;
    }

    private static MonitorCaps Caps(List<byte> joined)
    {
        int end = joined.IndexOf(0);
        string raw = NameScrub.Display(Encoding.ASCII.GetString([.. end >= 0 ? joined.GetRange(0, end) : joined]));
        var (inputs, reported) = Mccs.Inputs(raw);
        return new MonitorCaps(inputs, reported, raw);
    }

    // The length byte, the body and the checksum from the display's address and the register.
    private static byte[] Frame(ReadOnlySpan<byte> body)
    {
        var frame = new byte[body.Length + 2];
        frame[0] = (byte)(0x80 | body.Length);
        body.CopyTo(frame.AsSpan(1));
        byte check = DisplayAddress ^ HostAddress;
        for (int i = 0; i < frame.Length - 1; i++)
        {
            check ^= frame[i];
        }

        frame[^1] = check;
        return frame;
    }

    // A reply's checksum: 0x50 XOR every byte but the last.
    private static bool Checked(ReadOnlySpan<byte> reply)
    {
        byte check = 0x50;
        foreach (byte b in reply[..^1])
        {
            check ^= b;
        }

        return check == reply[^1];
    }
}
