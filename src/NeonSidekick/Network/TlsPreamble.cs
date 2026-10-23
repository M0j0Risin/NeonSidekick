using System.Buffers.Binary;
using System.Text;

namespace NeonSidekick.Network;

/// <summary>The talk before a TLS handshake on a port that does not start with one.</summary>
public enum StartTls
{
    /// <summary>TLS from the first byte (443, 636, 993, 995…).</summary>
    None,
    Smtp,
    Imap,
    Pop3,
    Ldap,

    /// <summary>RDP's X.224 negotiation (3389): not STARTTLS, but a preamble all the same.</summary>
    Rdp,
}

/// <summary>A server that will not go on to TLS: the sentence says why.</summary>
public sealed class TlsPreambleException(string message) : Exception(message);

/// <summary>
/// The preambles <c>net_tls</c> speaks before its handshake (2026-10-15, the admin-tools plan, the user's call: SMTP, IMAP, POP3, LDAP; RDP
/// added because 3389 is not TLS from the first byte either). Line-based and capped so a hostile server can neither stall nor flood it:
/// 1,024 bytes a line, 64 lines, 16 KB in all, the LDAP reply's BER length capped at 16 KB before it is read — all inside the caller's 10 s.
/// No credentials are ever sent and nothing past the handshake is read. Pure over a <see cref="Stream"/>, fixture-tested over a scripted one.
/// </summary>
public static class TlsPreamble
{
    public const int MaxLine = 1024;
    public const int MaxLines = 64;
    public const int MaxTotal = 16 * 1024;

    /// <summary>The word for each preamble, as <c>net_tls</c>'s <c>starttls</c> takes it.</summary>
    public static readonly IReadOnlyDictionary<string, StartTls> Words = new Dictionary<string, StartTls>(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = StartTls.None,
        ["smtp"] = StartTls.Smtp,
        ["imap"] = StartTls.Imap,
        ["pop3"] = StartTls.Pop3,
        ["ldap"] = StartTls.Ldap,
        ["rdp"] = StartTls.Rdp,
    };

    /// <summary>The preamble a port implies when <c>starttls</c> is left out: 25/587 SMTP, 143 IMAP, 110 POP3, 389 LDAP, 3389 RDP; else none.</summary>
    public static StartTls Guess(int port) => port switch
    {
        25 or 587 => StartTls.Smtp,
        143 => StartTls.Imap,
        110 => StartTls.Pop3,
        389 or 3268 => StartTls.Ldap,   // 3268: the global catalog's plain LDAP (2026-10-23, the admin review's P17)
        3389 => StartTls.Rdp,
        _ => StartTls.None,
    };

    /// <summary>The word for <paramref name="kind"/>.</summary>
    public static string Word(StartTls kind) => kind switch
    {
        StartTls.Smtp => "smtp",
        StartTls.Imap => "imap",
        StartTls.Pop3 => "pop3",
        StartTls.Ldap => "ldap",
        StartTls.Rdp => "rdp",
        _ => "none",
    };

    /// <summary>The LDAP StartTLS ExtendedRequest (RFC 4511 §4.14), message id 1: a fixed 31-byte BER packet.</summary>
    public static readonly byte[] LdapStartTls =
    [
        0x30, 0x1d, 0x02, 0x01, 0x01, 0x77, 0x18, 0x80, 0x16,
        .. "1.3.6.1.4.1.1466.20037"u8.ToArray(),
    ];

    /// <summary>
    /// RDP's Client X.224 Connection Request with an RDP Negotiation Request for <c>PROTOCOL_SSL | PROTOCOL_HYBRID</c> (MS-RDPBCGR 2.2.1.1):
    /// a fixed 19-byte packet — the TPKT header, the X.224 CR, the 8-byte negotiation request.
    /// </summary>
    public static readonly byte[] RdpConnectionRequest =
    [
        0x03, 0x00, 0x00, 0x13,
        0x0e, 0xe0, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00,
    ];

    /// <summary>Speaks <paramref name="kind"/>'s preamble on <paramref name="stream"/>; returns when TLS may start, throws <see cref="TlsPreambleException"/> when it may not.</summary>
    public static async Task RunAsync(Stream stream, StartTls kind, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new CappedReader(stream);
        switch (kind)
        {
            case StartTls.None:
                return;
            case StartTls.Smtp:
                await SmtpAsync(stream, reader, port, cancellationToken).ConfigureAwait(false);
                return;
            case StartTls.Imap:
                await ImapAsync(stream, reader, port, cancellationToken).ConfigureAwait(false);
                return;
            case StartTls.Pop3:
                await Pop3Async(stream, reader, port, cancellationToken).ConfigureAwait(false);
                return;
            case StartTls.Ldap:
                await LdapAsync(stream, reader, port, cancellationToken).ConfigureAwait(false);
                return;
            case StartTls.Rdp:
                await RdpAsync(stream, reader, port, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static async Task SmtpAsync(Stream stream, CappedReader reader, int port, CancellationToken cancellationToken)
    {
        var greeting = await ReadSmtpReplyAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!greeting.Code.StartsWith('2'))
        {
            throw new TlsPreambleException(NetworkText.PreambleRefused("SMTP", port, greeting.Last));
        }

        await WriteAsync(stream, "EHLO neonsidekick.invalid\r\n", cancellationToken).ConfigureAwait(false);
        var ehlo = await ReadSmtpReplyAsync(reader, cancellationToken).ConfigureAwait(false);
        if (!ehlo.Code.StartsWith('2') || !ehlo.Lines.Any(l => l.Length > 4 && l[4..].Trim().StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
        {
            throw new TlsPreambleException(NetworkText.NoStartTls(port));
        }

        await WriteAsync(stream, "STARTTLS\r\n", cancellationToken).ConfigureAwait(false);
        var go = await ReadSmtpReplyAsync(reader, cancellationToken).ConfigureAwait(false);
        if (go.Code != "220")
        {
            throw new TlsPreambleException(NetworkText.PreambleRefused("SMTP", port, go.Last));
        }
    }

    /// <summary>One SMTP reply: <c>250-…</c> lines up to the one with a space after its code.</summary>
    private static async Task<(string Code, IReadOnlyList<string> Lines, string Last)> ReadSmtpReplyAsync(CappedReader reader, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            lines.Add(line);
            if (line.Length < 3)
            {
                throw new TlsPreambleException(NetworkText.PreambleGarbled);
            }

            if (line.Length == 3 || line[3] == ' ')
            {
                return (line[..3], lines, line);
            }

            if (line[3] != '-')
            {
                throw new TlsPreambleException(NetworkText.PreambleGarbled);
            }
        }
    }

    private static async Task ImapAsync(Stream stream, CappedReader reader, int port, CancellationToken cancellationToken)
    {
        string greeting = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) && !greeting.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
        {
            throw new TlsPreambleException(NetworkText.PreambleRefused("IMAP", port, greeting));
        }

        await WriteAsync(stream, "a1 STARTTLS\r\n", cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (!line.StartsWith("a1 ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line[3..].StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new TlsPreambleException(line[3..].StartsWith("BAD", StringComparison.OrdinalIgnoreCase) ? NetworkText.NoStartTls(port) : NetworkText.PreambleRefused("IMAP", port, line));
        }
    }

    private static async Task Pop3Async(Stream stream, CappedReader reader, int port, CancellationToken cancellationToken)
    {
        string greeting = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!greeting.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new TlsPreambleException(NetworkText.PreambleRefused("POP3", port, greeting));
        }

        await WriteAsync(stream, "STLS\r\n", cancellationToken).ConfigureAwait(false);
        string answer = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (!answer.StartsWith("+OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new TlsPreambleException(NetworkText.NoStartTls(port));
        }
    }

    private static async Task LdapAsync(Stream stream, CappedReader reader, int port, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(LdapStartTls, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        byte tag = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (tag != 0x30)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        int length = await ReadBerLengthAsync(reader, cancellationToken).ConfigureAwait(false);
        var body = await reader.ReadBytesAsync(length, cancellationToken).ConfigureAwait(false);
        int code = LdapResultCode(body);
        if (code == LdapDisconnected)
        {
            throw new TlsPreambleException(NetworkText.LdapDisconnected(port));
        }

        if (code < 0)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        if (code != 0)
        {
            throw new TlsPreambleException(code == 2 ? NetworkText.NoStartTls(port) : NetworkText.LdapStartTlsRefused(port, code));
        }
    }

    /// <summary>A BER length: the short form, or the long form up to four bytes, capped at <see cref="MaxTotal"/>.</summary>
    private static async Task<int> ReadBerLengthAsync(CappedReader reader, CancellationToken cancellationToken)
    {
        byte first = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (first < 0x80)
        {
            return first;
        }

        int count = first & 0x7F;
        if (count == 0 || count > 4)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        long length = 0;
        for (int i = 0; i < count; i++)
        {
            length = (length << 8) | await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (length > MaxTotal)
        {
            throw new TlsPreambleException(NetworkText.PreambleTooLong);
        }

        return (int)length;
    }

    /// <summary><see cref="LdapResultCode"/>'s answer for a Notice of Disconnection (message id 0, RFC 4511 §4.4.1).</summary>
    public const int LdapDisconnected = -2;

    /// <summary>
    /// The resultCode of an LDAPMessage's body (past its outer SEQUENCE header): the messageID INTEGER, then the ExtendedResponse
    /// (<c>[APPLICATION 24]</c>, 0x78) whose first element is the ENUMERATED result. −1 when the body is not that shape.
    /// </summary>
    /// <remarks>
    /// The message id is ours, 1 (2026-10-23, the admin review's P12: an unsolicited Notice of Disconnection, id 0, was read as the StartTLS
    /// answer, its code 2 then "no STARTTLS"); id 0 is <see cref="LdapDisconnected"/>.
    /// </remarks>
    public static int LdapResultCode(ReadOnlySpan<byte> body)
    {
        int at = 0;
        if (body.Length >= 3 && body[0] == 0x02 && body[1] == 1 && body[2] == 0)
        {
            return LdapDisconnected;
        }

        if (body.Length < 3 || body[0] != 0x02 || body[1] != 1 || body[2] != 1 || !Skip(body, ref at, 0x02))
        {
            return -1;
        }

        if (at >= body.Length || body[at] != 0x78)
        {
            return -1;
        }

        at++;
        if (!SkipLength(body, ref at) || at + 3 > body.Length || body[at] != 0x0a || body[at + 1] != 1)
        {
            return -1;
        }

        return body[at + 2];
    }

    private static bool Skip(ReadOnlySpan<byte> body, ref int at, byte tag)
    {
        if (at >= body.Length || body[at] != tag)
        {
            return false;
        }

        at++;
        if (!SkipLength(body, ref at, out int length))
        {
            return false;
        }

        // Against what is left, never `at + length`: a hostile four-byte length (0x7FFFFFFF) wrapped `at` negative and the next read
        // indexed the body with it (2026-10-19, the network review).
        if (length > body.Length - at)
        {
            return false;
        }

        at += length;
        return true;
    }

    private static bool SkipLength(ReadOnlySpan<byte> body, ref int at) => SkipLength(body, ref at, out _);

    private static bool SkipLength(ReadOnlySpan<byte> body, ref int at, out int length)
    {
        length = 0;
        if (at >= body.Length)
        {
            return false;
        }

        byte first = body[at++];
        if (first < 0x80)
        {
            length = first;
            return true;
        }

        int count = first & 0x7F;
        if (count == 0 || count > 4 || at + count > body.Length)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            length = (length << 8) | body[at++];
        }

        return length >= 0;
    }

    private static async Task RdpAsync(Stream stream, CappedReader reader, int port, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(RdpConnectionRequest, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var header = await reader.ReadBytesAsync(4, cancellationToken).ConfigureAwait(false);
        if (header[0] != 0x03)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        int length = (header[2] << 8) | header[3];
        if (length < 11 || length > MaxTotal)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        var rest = await reader.ReadBytesAsync(length - 4, cancellationToken).ConfigureAwait(false);
        int outcome = RdpNegotiation(rest);
        switch (outcome)
        {
            case > 0:
                return;
            case 0:
                throw new TlsPreambleException(NetworkText.RdpNoTls(port));
            default:
                throw new TlsPreambleException(NetworkText.RdpRefused(port, -outcome));
        }
    }

    /// <summary>
    /// What an RDP Connection Confirm (past its TPKT header) negotiated: the selected protocols (&gt; 0, TLS or CredSSP) from an
    /// RDP_NEG_RSP, 0 for none (standard RDP security, no TLS), or minus the failure code of an RDP_NEG_FAILURE.
    /// </summary>
    public static int RdpNegotiation(ReadOnlySpan<byte> confirm)
    {
        // X.224 Connection Confirm: LI, 0xD0, dst-ref(2), src-ref(2), class; then the 8-byte negotiation data when present.
        if (confirm.Length < 7 || (confirm[1] & 0xF0) != 0xD0)
        {
            throw new TlsPreambleException(NetworkText.PreambleGarbled);
        }

        if (confirm.Length < 15)
        {
            return 0;
        }

        // Read unsigned and kept to the low bits (2026-10-23, the admin review's P13): a set high bit made a selected protocol read as a
        // failure, and -Math.Max(1, int.MinValue) overflowed. The protocols are the low four bits (TLS, CredSSP, RDSTLS, CredSSP with early
        // user auth); the failure codes are small numbers, held to 1..0xFFFF.
        var negotiation = confirm[7..];
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(negotiation[4..8]);
        return negotiation[0] switch
        {
            0x02 => (int)(value & 0xF),
            0x03 => -(int)Math.Clamp(value, 1u, 0xFFFFu),
            _ => 0,
        };
    }

    private static async Task WriteAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The capped reader under every preamble: bytes one at a time, so nothing past the preamble is ever taken off the stream.</summary>
    private sealed class CappedReader(Stream stream)
    {
        private readonly byte[] _one = new byte[1];
        private int _total;
        private int _lines;

        public async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (++_total > MaxTotal)
            {
                throw new TlsPreambleException(NetworkText.PreambleTooLong);
            }

            int read = await stream.ReadAsync(_one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new TlsPreambleException(NetworkText.PreambleClosed);
            }

            return _one[0];
        }

        public async Task<byte[]> ReadBytesAsync(int count, CancellationToken cancellationToken)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++)
            {
                bytes[i] = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            }

            return bytes;
        }

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (++_lines > MaxLines)
            {
                throw new TlsPreambleException(NetworkText.PreambleTooLong);
            }

            var line = new List<byte>(128);
            while (true)
            {
                byte b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (b == (byte)'\n')
                {
                    if (line.Count > 0 && line[^1] == (byte)'\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.ASCII.GetString(line.ToArray());
                }

                line.Add(b);
                if (line.Count > MaxLine)
                {
                    throw new TlsPreambleException(NetworkText.PreambleTooLong);
                }
            }
        }
    }
}
